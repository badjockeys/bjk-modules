using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Moderation;

/// <summary>
/// Gives or removes a badge on a profile (by @handle), with a reason for the moderation audit log. Moderators and
/// admins. Trusted reporter is only ever given this way; the others can be removed this way if given in error.
/// </summary>
public sealed record SetBadgeCommand(string Handle, Badge Badge, bool Award, string Reason) : IRequest<IReadOnlyList<Badge>>;

public sealed class SetBadgeCommandValidator : AbstractValidator<SetBadgeCommand>
{
    public SetBadgeCommandValidator()
    {
        RuleFor(c => c.Handle).NotEmpty();
        RuleFor(c => c.Badge).IsInEnum();
        RuleFor(c => c.Badge)
            .Equal(Badge.TrustedReporter)
            .When(c => c.Award)
            .WithMessage("Only Trusted reporter is given by moderators; the other badges are given automatically.");
        RuleFor(c => c.Reason)
            .NotEmpty()
            .WithMessage("Give a reason. It's recorded in the moderation audit log.")
            .MaximumLength(ResetProfileCommandValidator.ReasonMaxLength);
    }
}

public sealed class SetBadgeCommandHandler(
    IProfileRepository profiles,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SetBadgeCommand, IReadOnlyList<Badge>>
{
    public async Task<IReadOnlyList<Badge>> Handle(SetBadgeCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(ProfilesModule.ManageScope);
        var profile = await ModeratedProfiles.GetAsync(profiles, request.Handle, cancellationToken);
        var existing = await profiles.GetBadgeAsync(profile.Id, request.Badge, cancellationToken);
        var changed = false;
        if (request.Award && existing is null)
        {
            profiles.AddBadge(ProfileBadge.Award(profile.Id, request.Badge, clock.GetUtcNow(), caller.UserId()));
            changed = true;
        }
        else if (!request.Award && existing is not null)
        {
            profiles.RemoveBadge(existing);
            changed = true;
        }

        if (changed)
        {
            await profiles.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(
                new ModerationAuditEntry(
                    "Profiles",
                    request.Award ? ModerationActions.AwardBadge : ModerationActions.RemoveBadge,
                    "Profile",
                    profile.Id.ToString(),
                    request.Reason.Trim(),
                    $"Badge={request.Badge}; Held={!request.Award}",
                    $"Badge={request.Badge}; Held={request.Award}"),
                cancellationToken);
        }

        return await profiles.ListBadgesAsync(profile.Id, cancellationToken);
    }
}
