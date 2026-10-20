using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Update;

/// <summary>Changes the signed-in user's display name, bio and location.</summary>
public sealed record UpdateProfileCommand(string DisplayName, string? Bio, string? Location)
    : IRequest<ProfileDto>, IProfileDetails;

public sealed class UpdateProfileCommandValidator : ProfileDetailsValidator<UpdateProfileCommand>;

public sealed class UpdateProfileCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<UpdateProfileCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(UpdateProfileCommand request, CancellationToken cancellationToken)
    {
        var profile = await MyProfile.GetAsync(profiles, caller, cancellationToken);

        profile.Update(request.DisplayName, request.Bio, request.Location, clock.GetUtcNow());
        profile.RecordAgeBand(caller.IsMinor);
        await profiles.SaveChangesAsync(cancellationToken);

        return await Avatars.ToDtoAsync(media, profile, cancellationToken);
    }
}

/// <summary>
/// Sets how the signed-in user uses the road (Driver, Cyclist or Both; null to not say). Optional and private: it
/// tailors their defaults (types first in lists, area alerts) and counts towards totals on the heat map. Others only
/// see it, on the profile and posts, if <c>Show</c> is true.
/// </summary>
public sealed record SetRoadUserCommand(RoadUser? RoadUser, bool Show = false) : IRequest<ProfileDto>;

public sealed class SetRoadUserCommandValidator : AbstractValidator<SetRoadUserCommand>
{
    public SetRoadUserCommandValidator()
    {
        RuleFor(c => c.RoadUser).IsInEnum().When(c => c.RoadUser is not null);
    }
}

public sealed class SetRoadUserCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SetRoadUserCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(SetRoadUserCommand request, CancellationToken cancellationToken)
    {
        var profile = await MyProfile.GetAsync(profiles, caller, cancellationToken);
        profile.SetRoadUser(request.RoadUser, request.Show, clock.GetUtcNow());
        await profiles.SaveChangesAsync(cancellationToken);
        return await Avatars.ToDtoAsync(media, profile, cancellationToken);
    }
}

/// <summary>Changes the signed-in user's @handle. The old handle becomes free for others.</summary>
public sealed record ChangeHandleCommand(string Handle) : IRequest<ProfileDto>;

public sealed class ChangeHandleCommandValidator : AbstractValidator<ChangeHandleCommand>
{
    public ChangeHandleCommandValidator()
    {
        RuleFor(c => c.Handle).ValidHandle();
    }
}

public sealed class ChangeHandleCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ChangeHandleCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(ChangeHandleCommand request, CancellationToken cancellationToken)
    {
        var profile = await MyProfile.GetAsync(profiles, caller, cancellationToken);
        var handle = Domain.Handle.Normalise(request.Handle);

        if (handle != profile.Handle)
        {
            var holder = await profiles.GetByHandleAsync(handle, cancellationToken);
            if (holder is not null)
            {
                throw ProfileRules.Invalid(nameof(request.Handle), "That handle is taken.");
            }

            profile.ChangeHandle(handle, clock.GetUtcNow());
            await profiles.SaveChangesAsync(cancellationToken);
        }

        return await Avatars.ToDtoAsync(media, profile, cancellationToken);
    }
}

/// <summary>
/// Sets the signed-in user's avatar to one of their own Ready images, or removes it when
/// <c>MediaId</c> is null. The previous avatar's files are deleted.
/// </summary>
public sealed record SetAvatarCommand(Guid? MediaId) : IRequest<ProfileDto>;

public sealed class SetAvatarCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SetAvatarCommand, ProfileDto>
{
    public async Task<ProfileDto> Handle(SetAvatarCommand request, CancellationToken cancellationToken)
    {
        var profile = await MyProfile.GetAsync(profiles, caller, cancellationToken);

        if (request.MediaId is { } mediaId)
        {
            var found = await media.GetAsync([mediaId], cancellationToken);
            var image = found.FirstOrDefault(m => m.Id == mediaId);
            if (image is null || image.UserId != profile.Id)
            {
                throw ProfileRules.Invalid(nameof(request.MediaId), "That isn't one of your uploads.");
            }

            if (image.MediaType != MediaType.Image)
            {
                throw ProfileRules.Invalid(nameof(request.MediaId), "Avatars must be photos.");
            }

            if (!image.IsReady)
            {
                throw ProfileRules.Invalid(nameof(request.MediaId), "That photo is still processing. Try again in a moment.");
            }
        }

        var previous = profile.SetAvatar(request.MediaId, clock.GetUtcNow());
        await profiles.SaveChangesAsync(cancellationToken);

        if (previous is { } oldAvatar)
        {
            await media.DeleteAsync([oldAvatar], cancellationToken);
        }

        return await Avatars.ToDtoAsync(media, profile, cancellationToken);
    }
}

internal static class MyProfile
{
    public static async Task<Profile> GetAsync(
        IProfileRepository profiles,
        ICurrentUser caller,
        CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        return await profiles.GetByIdAsync(userId, cancellationToken)
            ?? throw new NotFoundException("Profile", "me");
    }
}
