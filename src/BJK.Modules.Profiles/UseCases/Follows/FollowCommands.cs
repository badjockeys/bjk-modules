using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Follows;

/// <summary>Whether the signed-in user now follows someone, and how many followers that person has.</summary>
public sealed record FollowStateDto(Guid UserId, bool Following, int FollowerCount);

/// <summary>A person in a list of followers, people followed or suggestions. <c>IsFollowing</c>: you follow them (null when signed out or yourself).</summary>
public sealed record FollowProfileDto(Guid Id, string Handle, string DisplayName, Uri? AvatarUrl, string? Bio, bool? IsFollowing);

/// <summary>
/// Follow someone, by @handle or user id: their posts show in your Following timeline, and they're told (by their
/// notification settings). Needs your own profile. Following someone already followed does nothing. Nobody can follow
/// across a block, either way.
/// </summary>
public sealed record FollowUserCommand(string User) : IRequest<FollowStateDto>;

/// <summary>Stop following someone, by @handle or user id. Does nothing if you weren't.</summary>
public sealed record UnfollowUserCommand(string User) : IRequest<FollowStateDto>;

public sealed class FollowUserCommandValidator : AbstractValidator<FollowUserCommand>
{
    public FollowUserCommandValidator() => RuleFor(c => c.User).NotEmpty();
}

public sealed class FollowUserCommandHandler(
    IProfileRepository profiles,
    ICurrentUser caller,
    TimeProvider clock,
    INotificationSender notifications)
    : IRequestHandler<FollowUserCommand, FollowStateDto>
{
    public async Task<FollowStateDto> Handle(FollowUserCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var target = await BlockTargets.FindAsync(profiles, request.User, cancellationToken);
        if (target is not { IsActive: true })
        {
            throw new NotFoundException("Profile", request.User);
        }

        if (target.Id == userId)
        {
            throw ProfileRules.Invalid(nameof(request.User), "You can't follow yourself.");
        }

        if (await profiles.GetByIdAsync(userId, cancellationToken) is null)
        {
            throw ProfileRules.Invalid("Profile", "Create your profile before following people.");
        }

        if (await profiles.IsBlockedAsync(userId, target.Id, cancellationToken)
            || await profiles.IsBlockedAsync(target.Id, userId, cancellationToken))
        {
            throw ProfileRules.Invalid(nameof(request.User), "You can't follow this account.");
        }

        var follow = await profiles.GetFollowAsync(userId, target.Id, cancellationToken);
        if (follow is null)
        {
            if ((await profiles.GetFollowingIdsAsync(userId, cancellationToken)).Count >= Follow.MaxPerUser)
            {
                throw ProfileRules.Invalid(nameof(request.User), $"You can follow up to {Follow.MaxPerUser} people. Unfollow someone first.");
            }

            profiles.AddFollow(Follow.Create(userId, target.Id, clock.GetUtcNow()));
            await profiles.SaveChangesAsync(cancellationToken);
            await notifications.NotifyAsync(new NotificationRequest(target.Id, NotificationType.NewFollower, ActorId: userId), cancellationToken);
        }

        var (followers, _) = await profiles.CountFollowsAsync(target.Id, cancellationToken);
        return new FollowStateDto(target.Id, true, followers);
    }
}

public sealed class UnfollowUserCommandHandler(IProfileRepository profiles, ICurrentUser caller)
    : IRequestHandler<UnfollowUserCommand, FollowStateDto>
{
    public async Task<FollowStateDto> Handle(UnfollowUserCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var target = await BlockTargets.FindAsync(profiles, request.User, cancellationToken)
            ?? throw new NotFoundException("Profile", request.User);

        var follow = await profiles.GetFollowAsync(userId, target.Id, cancellationToken);
        if (follow is not null)
        {
            profiles.RemoveFollow(follow);
            await profiles.SaveChangesAsync(cancellationToken);
        }

        var (followers, _) = await profiles.CountFollowsAsync(target.Id, cancellationToken);
        return new FollowStateDto(target.Id, false, followers);
    }
}

/// <summary>A page of someone's followers (<c>Followers</c>) or of the people they follow, most recent first. Anyone can see these.</summary>
public sealed record ListFollowsQuery(string Handle, bool Followers, int Page = 1, int PageSize = 20) : IRequest<PagedResult<FollowProfileDto>>;

public sealed class ListFollowsQueryValidator : AbstractValidator<ListFollowsQuery>
{
    public ListFollowsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListFollowsQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser caller)
    : IRequestHandler<ListFollowsQuery, PagedResult<FollowProfileDto>>
{
    public async Task<PagedResult<FollowProfileDto>> Handle(ListFollowsQuery request, CancellationToken cancellationToken)
    {
        var handle = Domain.Handle.Normalise(request.Handle);
        var owner = Domain.Handle.IsValid(handle) ? await profiles.GetByHandleAsync(handle, cancellationToken) : null;
        if (owner is not { IsActive: true })
        {
            throw new NotFoundException("Profile", request.Handle);
        }

        var (items, total) = await profiles.ListFollowsAsync(owner.Id, request.Followers, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<FollowProfileDto>(
            await FollowViews.BuildAsync(profiles, media, caller.UserId(), items, cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>
/// Who to follow: people whose home area is near yours, nearest first, leaving out people you follow or have blocked
/// (either way). Only adults (known to be 18 or over) are ever suggested, and under-18s don't get suggestions.
/// <c>HasHomeArea</c> is false when you haven't chosen a home area yet (then there are no suggestions).
/// </summary>
public sealed record WhoToFollowQuery(int Limit = 3) : IRequest<WhoToFollowDto>;

public sealed record WhoToFollowDto(IReadOnlyList<FollowProfileDto> People, bool HasHomeArea);

public sealed class WhoToFollowQueryValidator : AbstractValidator<WhoToFollowQuery>
{
    public WhoToFollowQueryValidator() => RuleFor(q => q.Limit).InclusiveBetween(1, 20);
}

public sealed class WhoToFollowQueryHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    IHomeAreas homeAreas,
    ICurrentUser caller)
    : IRequestHandler<WhoToFollowQuery, WhoToFollowDto>
{
    /// <summary>How far around your home area people are looked for.</summary>
    public const int SearchKm = 25;

    /// <summary>How many nearby people are considered before filtering.</summary>
    public const int Candidates = 200;

    public async Task<WhoToFollowDto> Handle(WhoToFollowQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var area = await homeAreas.GetAsync(userId, cancellationToken);
        if (area is null)
        {
            return new WhoToFollowDto([], false);
        }

        if (caller.IsMinor)
        {
            return new WhoToFollowDto([], true);
        }

        var near = await homeAreas.UsersNearAsync(area.Latitude, area.Longitude, Math.Max(area.RadiusKm, SearchKm), Candidates, cancellationToken);
        var following = (await profiles.GetFollowingIdsAsync(userId, cancellationToken)).ToHashSet();
        var blocked = (await profiles.GetBlockedEitherWayAsync(userId, cancellationToken)).ToHashSet();
        var ids = near.Where(id => id != userId && !following.Contains(id) && !blocked.Contains(id)).ToList();
        var found = (await profiles.GetSuggestableAsync(ids, cancellationToken)).ToDictionary(p => p.Id);
        var people = ids.Where(found.ContainsKey).Select(id => found[id]).Take(request.Limit).ToList();

        return new WhoToFollowDto(await FollowViews.BuildAsync(profiles, media, userId, people, cancellationToken), true);
    }
}

internal static class FollowViews
{
    public static async Task<IReadOnlyList<FollowProfileDto>> BuildAsync(
        IProfileRepository profiles,
        IMediaDirectory media,
        Guid? viewerId,
        IReadOnlyList<Profile> people,
        CancellationToken cancellationToken)
    {
        var avatars = await Avatars.GetUrlsAsync(media, people, cancellationToken);
        var following = viewerId is { } id
            ? await profiles.FollowingAmongAsync(id, people.Select(p => p.Id).ToList(), cancellationToken)
            : [];
        return people
            .Select(p => new FollowProfileDto(
                p.Id,
                p.Handle,
                p.DisplayName,
                Avatars.UrlFor(p, avatars),
                p.Bio,
                viewerId is null || viewerId == p.Id ? null : following.Contains(p.Id)))
            .ToList();
    }
}
