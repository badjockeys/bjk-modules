using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
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
/// Who to follow (owner, 22 Oct 2026): the people who posted most in the last 90 days within 25 km of your home area
/// (or your area's radius, if larger), or of where you usually post if you haven't chosen one; boosted by the
/// reactions and comments those posts got, and by how many of the people you follow follow them. When there aren't
/// enough nearby (or you've no area and haven't posted), the most active posters UK-wide fill the list. Never you,
/// people you follow, people blocked either way, or anyone not known to be 18 or over; under-18s get no suggestions.
/// <c>HasHomeArea</c> is false when you haven't chosen a home area (the app can ask for one; suggestions still come).
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
    IActivePosters posters,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<WhoToFollowQuery, WhoToFollowDto>
{
    /// <summary>How far around your area posters are looked for.</summary>
    public const int SearchKm = 25;

    /// <summary>How far back posting counts.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromDays(90);

    /// <summary>How many active posters are considered before filtering.</summary>
    public const int Candidates = 200;

    public async Task<WhoToFollowDto> Handle(WhoToFollowQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var area = await homeAreas.GetAsync(userId, cancellationToken);
        if (caller.IsMinor)
        {
            return new WhoToFollowDto([], area is not null);
        }

        var since = clock.GetUtcNow() - Period;
        (decimal Latitude, decimal Longitude)? centre = area is not null
            ? (area.Latitude, area.Longitude)
            : await posters.UsualPlaceAsync(userId, since, cancellationToken);
        var km = Math.Max(area?.RadiusKm ?? 0, SearchKm);

        var following = (await profiles.GetFollowingIdsAsync(userId, cancellationToken)).ToHashSet();
        var blocked = (await profiles.GetBlockedEitherWayAsync(userId, cancellationToken)).ToHashSet();
        bool Allowed(ActivePoster p) => p.UserId != userId && !following.Contains(p.UserId) && !blocked.Contains(p.UserId);

        var nearby = centre is { } c
            ? (await posters.MostActiveAsync(c.Latitude, c.Longitude, km, since, Candidates, cancellationToken)).Where(Allowed).ToList()
            : new List<ActivePoster>();
        var ranked = await RankAsync(nearby, following, cancellationToken);
        var people = await AdultsAsync(ranked, request.Limit, cancellationToken);

        if (people.Count < request.Limit)
        {
            // Not enough nearby: the most active posters anywhere fill the rest.
            var taken = people.Select(p => p.Id).ToHashSet();
            var anywhere = (await posters.MostActiveAsync(null, null, km, since, Candidates, cancellationToken))
                .Where(p => Allowed(p) && !taken.Contains(p.UserId))
                .ToList();
            people.AddRange(await AdultsAsync(await RankAsync(anywhere, following, cancellationToken), request.Limit - people.Count, cancellationToken));
        }

        return new WhoToFollowDto(await FollowViews.BuildAsync(profiles, media, userId, people, cancellationToken), area is not null);
    }

    /// <summary>
    /// Each poster's score: 10 a post, plus 1 for every 4 reactions and comments (up to 50), plus 15 for each person
    /// you follow who follows them (up to 3). Highest first.
    /// </summary>
    internal async Task<List<Guid>> RankAsync(IReadOnlyList<ActivePoster> candidates, IReadOnlyCollection<Guid> following, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var friends = await profiles.CountFollowedByAsync(following, candidates.Select(c => c.UserId).ToList(), cancellationToken);
        return candidates
            .Select(c => (c.UserId, Score: Score(c, friends.GetValueOrDefault(c.UserId))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.UserId)
            .Select(x => x.UserId)
            .ToList();
    }

    internal static double Score(ActivePoster poster, int followedByYourFollows) =>
        (poster.Posts * 10) + Math.Min(poster.Engagement / 4.0, 50) + (Math.Min(followedByYourFollows, 3) * 15);

    /// <summary>The first <paramref name="take"/> of <paramref name="ranked"/> who are active adults, in order.</summary>
    private async Task<List<Profile>> AdultsAsync(List<Guid> ranked, int take, CancellationToken cancellationToken)
    {
        if (ranked.Count == 0 || take <= 0)
        {
            return [];
        }

        var found = (await profiles.GetSuggestableAsync(ranked, cancellationToken)).ToDictionary(p => p.Id);
        return ranked.Where(found.ContainsKey).Select(id => found[id]).Take(take).ToList();
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
