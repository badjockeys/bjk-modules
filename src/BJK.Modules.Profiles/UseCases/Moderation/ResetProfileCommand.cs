using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Moderation;

/// <summary>
/// Any profile by @handle, including deactivated ones. Moderators and admins (<c>bjk.profiles.manage</c>).
/// </summary>
public sealed record GetProfileForModerationQuery(string Handle) : IRequest<ModeratedProfileDto>;

/// <summary>A profile as moderators see it.</summary>
public sealed record ModeratedProfileDto(ProfileDto Profile, DateTimeOffset? DeactivatedAt);

/// <summary>
/// Resets what a user wrote on their profile (display name back to the handle; bio, location and avatar removed),
/// for offensive or identifying profiles. A reason is required and the action is audited. Moderators and admins.
/// </summary>
public sealed record ResetProfileCommand(string Handle, string Reason) : IRequest<ProfileDto>;

public sealed class ResetProfileCommandValidator : AbstractValidator<ResetProfileCommand>
{
    public const int ReasonMaxLength = 500;

    public ResetProfileCommandValidator()
    {
        RuleFor(c => c.Handle).NotEmpty();
        RuleFor(c => c.Reason)
            .NotEmpty()
            .WithMessage("Give a reason. It's recorded in the moderation audit log.")
            .MaximumLength(ReasonMaxLength);
    }
}

public sealed class GetProfileForModerationQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser caller)
    : IRequestHandler<GetProfileForModerationQuery, ModeratedProfileDto>
{
    public async Task<ModeratedProfileDto> Handle(GetProfileForModerationQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(ProfilesModule.ManageScope);
        var profile = await ModeratedProfiles.GetAsync(profiles, request.Handle, cancellationToken);
        return new ModeratedProfileDto(await Avatars.ToDtoAsync(media, profile, cancellationToken), profile.DeactivatedAt);
    }
}

public sealed class ResetProfileCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ResetProfileCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(ResetProfileCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(ProfilesModule.ManageScope);
        var profile = await ModeratedProfiles.GetAsync(profiles, request.Handle, cancellationToken);

        var before = $"Bio={(profile.Bio is null ? "empty" : "set")}; Location={(profile.Location is null ? "empty" : "set")}; Avatar={(profile.AvatarMediaId is null ? "none" : "set")}; DisplayName={(profile.DisplayName == profile.Handle ? "handle" : "custom")}";
        var oldAvatar = profile.ResetPublicDetails(clock.GetUtcNow());
        await profiles.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Profiles",
                ModerationActions.ResetProfile,
                "Profile",
                profile.Id.ToString(),
                request.Reason.Trim(),
                before,
                "Bio=empty; Location=empty; Avatar=none; DisplayName=handle"),
            cancellationToken);

        if (oldAvatar is { } avatarId)
        {
            await media.DeleteAsync([avatarId], cancellationToken);
        }

        return ProfileDto.From(profile, avatarUrl: null);
    }
}

internal static class ModeratedProfiles
{
    public static async Task<Domain.Profile> GetAsync(IProfileRepository profiles, string handle, CancellationToken cancellationToken)
    {
        var normalised = Domain.Handle.Normalise(handle);
        var profile = Domain.Handle.IsValid(normalised)
            ? await profiles.GetByHandleAsync(normalised, cancellationToken)
            : null;
        return profile ?? throw new NotFoundException("Profile", handle);
    }
}
