using BJK.Modules.Posts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Reels;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Engagement;

/// <summary>One answer a vote offers, and its wording for that vote.</summary>
public sealed record VoteAnswerDto(VoteChoice Choice, string Label);

/// <summary>
/// A post's vote as the viewer may see it. <c>Question</c> and <c>Answers</c> (in order) come from its main
/// subcategory (owner, 23 Oct 2026: votes fit the category). <c>Results</c> (votes per answer) is null while hidden: until the viewer votes, for everyone except the
/// author; for signed-out visitors until there are <see cref="PostVoting.PublicResultsAfter"/> votes. Closed votes show
/// results to everyone. <c>ClosesAt</c> is null when the vote never closes.
/// </summary>
public sealed record VoteSummaryDto(
    string Question,
    IReadOnlyList<VoteAnswerDto> Answers,
    int Total,
    IReadOnlyDictionary<VoteChoice, int>? Results,
    VoteChoice? MyChoice,
    bool IsClosed,
    DateTimeOffset? ClosesAt,
    bool CanVote)
{
    /// <summary>The vote as this viewer sees it.</summary>
    public static VoteSummaryDto For(Post post, VoteTally tally, Guid? viewerId, DateTimeOffset now)
    {
        var vote = post.Vote;

        // Only answers to this question count: a vote cast before the categories changed (or before the author changed
        // the main subcategory) answered a different question, so it's left out and that voter can vote again.
        var mine = tally.Mine is { } m && vote.Offers(m) ? m : (VoteChoice?)null;
        var total = vote.Answers.Sum(a => tally.Counts.GetValueOrDefault(a.Choice));
        var closed = post.IsVoteClosed(now);
        var isAuthor = viewerId == post.AuthorId;
        var show = closed
            || isAuthor
            || mine is not null
            || (viewerId is null && total >= PostVoting.PublicResultsAfter);
        return new VoteSummaryDto(
            vote.Question,
            vote.Answers.Select(a => new VoteAnswerDto(a.Choice, a.Label)).ToList(),
            total,
            show ? vote.Answers.ToDictionary(a => a.Choice, a => tally.Counts.GetValueOrDefault(a.Choice)) : null,
            mine,
            closed,
            post.VoteClosesAt,
            CanVote: viewerId is not null && !isAuthor && !closed && post.IsPublic);
    }
}

/// <summary>Votes on a post, or changes the caller's vote, until it closes. Not on your own post.</summary>
public sealed record CastVoteCommand(Guid PostId, VoteChoice Choice) : IRequest<VoteSummaryDto>;

public sealed class CastVoteCommandValidator : AbstractValidator<CastVoteCommand>
{
    public CastVoteCommandValidator()
    {
        RuleFor(c => c.Choice).IsInEnum();
    }
}

public sealed class CastVoteCommandHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    IBlockDirectory blocks,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CastVoteCommand, VoteSummaryDto>
{
    public async Task<VoteSummaryDto> Handle(CastVoteCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);
        var now = clock.GetUtcNow();

        if (post.AuthorId == userId)
        {
            throw PostAccess.Invalid(nameof(request.Choice), "You can't vote on your own post.");
        }

        if (post.IsVoteClosed(now))
        {
            throw PostAccess.Invalid(nameof(request.Choice), "Voting on this post has closed.");
        }

        if (!post.Vote.Offers(request.Choice))
        {
            throw PostAccess.Invalid(nameof(request.Choice), "That answer doesn't fit this post's vote.");
        }

        var existing = await engagement.GetVoteAsync(post.Id, userId, cancellationToken);
        if (existing is null)
        {
            engagement.AddVote(PostVote.Create(post.Id, userId, request.Choice, now));
        }
        else if (existing.Choice != request.Choice)
        {
            existing.Change(request.Choice, now);
        }

        await engagement.SaveChangesAsync(cancellationToken);
        var tallies = await engagement.GetVoteTalliesAsync([post.Id], userId, cancellationToken);
        return VoteSummaryDto.For(post, tallies[post.Id], userId, now);
    }
}

/// <summary>Whether the caller follows a post now.</summary>
public sealed record PostFollowStateDto(Guid PostId, bool Following);

/// <summary>
/// Follows a post: the caller hears about new comments and the vote result. Safe to repeat. Authors always follow their
/// own posts, so they can't follow or unfollow them (400).
/// </summary>
public sealed record FollowPostCommand(Guid PostId) : IRequest<PostFollowStateDto>;

public sealed class FollowPostCommandHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    IBlockDirectory blocks,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<FollowPostCommand, PostFollowStateDto>
{
    /// <summary>Why following or unfollowing your own post is refused.</summary>
    public const string OwnPost = "You always follow your own posts.";

    public async Task<PostFollowStateDto> Handle(FollowPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        if (post.IsOwnedBy(userId))
        {
            throw PostAccess.Invalid(nameof(request.PostId), OwnPost);
        }

        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);
        await engagement.EnsureFollowingAsync(post.Id, userId, clock.GetUtcNow(), cancellationToken);
        return new PostFollowStateDto(post.Id, true);
    }
}

/// <summary>Stops following a post. Safe to repeat. Not for your own posts (400).</summary>
public sealed record UnfollowPostCommand(Guid PostId) : IRequest<PostFollowStateDto>;

public sealed class UnfollowPostCommandHandler(IPostRepository posts, IEngagementRepository engagement, ICurrentUser caller)
    : IRequestHandler<UnfollowPostCommand, PostFollowStateDto>
{
    public async Task<PostFollowStateDto> Handle(UnfollowPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        if (await posts.GetAsync(request.PostId, cancellationToken) is { } post && post.IsOwnedBy(userId))
        {
            throw PostAccess.Invalid(nameof(request.PostId), FollowPostCommandHandler.OwnPost);
        }

        if (await engagement.GetFollowAsync(request.PostId, userId, cancellationToken) is { } follow)
        {
            engagement.RemoveFollow(follow);
            await engagement.SaveChangesAsync(cancellationToken);
        }

        return new PostFollowStateDto(request.PostId, false);
    }
}

/// <summary>Whether the caller has saved a post now.</summary>
public sealed record BookmarkStateDto(Guid PostId, bool Saved);

/// <summary>Saves a post to the caller's bookmarks (private to them). Safe to repeat.</summary>
public sealed record SavePostCommand(Guid PostId) : IRequest<BookmarkStateDto>;

public sealed class SavePostCommandHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SavePostCommand, BookmarkStateDto>
{
    /// <summary>The most posts one person can save.</summary>
    public const int MaxBookmarks = 1000;

    public async Task<BookmarkStateDto> Handle(SavePostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        if (await engagement.GetBookmarkAsync(userId, post.Id, cancellationToken) is null)
        {
            var (_, total) = await engagement.ListBookmarkedAsync(userId, 1, 1, cancellationToken);
            if (total >= MaxBookmarks)
            {
                throw PostAccess.Invalid("Bookmarks", $"You can save up to {MaxBookmarks} posts. Remove some first.");
            }

            engagement.AddBookmark(Bookmark.Create(userId, post.Id, clock.GetUtcNow()));
            await engagement.SaveChangesAsync(cancellationToken);
        }

        return new BookmarkStateDto(post.Id, true);
    }
}

/// <summary>Removes a post from the caller's bookmarks. Safe to repeat.</summary>
public sealed record UnsavePostCommand(Guid PostId) : IRequest<BookmarkStateDto>;

public sealed class UnsavePostCommandHandler(IEngagementRepository engagement, ICurrentUser caller)
    : IRequestHandler<UnsavePostCommand, BookmarkStateDto>
{
    public async Task<BookmarkStateDto> Handle(UnsavePostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        if (await engagement.GetBookmarkAsync(userId, request.PostId, cancellationToken) is { } bookmark)
        {
            engagement.RemoveBookmark(bookmark);
            await engagement.SaveChangesAsync(cancellationToken);
        }

        return new BookmarkStateDto(request.PostId, false);
    }
}

/// <summary>The caller's saved posts, most recently saved first. Posts since removed are left out.</summary>
public sealed record ListBookmarksQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListBookmarksQueryValidator : AbstractValidator<ListBookmarksQuery>
{
    public ListBookmarksQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListBookmarksQueryHandler(IEngagementRepository engagement, PostViews views, ICurrentUser caller)
    : IRequestHandler<ListBookmarksQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListBookmarksQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (items, total) = await engagement.ListBookmarkedAsync(userId, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<PostDto>(await views.BuildAsync(items, userId, cancellationToken), request.Page, request.PageSize, total);
    }
}

/// <summary>
/// Records that the viewer saw these posts (in a feed for a second or more, or opened), counted once per viewer per
/// post. Anyone can call it; signed-out viewers are identified as for reels. Authors' views of their own posts, and
/// posts that aren't public, aren't counted. Returns how many new views were counted.
/// </summary>
public sealed record RecordViewsCommand(IReadOnlyList<Guid> PostIds, ViewerContext Viewer) : IRequest<int>;

public sealed class RecordViewsCommandValidator : AbstractValidator<RecordViewsCommand>
{
    public const int MaxPerCall = 50;

    public RecordViewsCommandValidator()
    {
        RuleFor(c => c.PostIds).NotNull().Must(ids => ids.Count is > 0 and <= MaxPerCall)
            .WithMessage($"Send 1 to {MaxPerCall} post ids.");
    }
}

public sealed class RecordViewsCommandHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    ICurrentUser caller,
    IOptions<ReelsOptions> options,
    TimeProvider clock,
    IHomeAreas? homeAreas = null)
    : IRequestHandler<RecordViewsCommand, int>
{
    public async Task<int> Handle(RecordViewsCommand request, CancellationToken cancellationToken)
    {
        var viewer = Viewers.Resolve(caller, request.Viewer, options.Value);
        var userId = caller.UserId();
        var found = await posts.GetManyAsync(request.PostIds.Distinct().ToList(), cancellationToken);
        var home = userId is { } id && homeAreas is not null ? await homeAreas.GetAsync(id, cancellationToken) : null;

        var views = found
            .Where(p => p.IsPublic && p.AuthorId != userId)
            .Select(p => new ViewToRecord(
                p.Id,
                viewer.Key,
                home is not null && GeoDistance.Km(home.Latitude, home.Longitude, p.Latitude, p.Longitude) <= PostView.LocalKm))
            .ToList();
        return views.Count == 0 ? 0 : await engagement.RecordViewsAsync(views, clock.GetUtcNow(), cancellationToken);
    }
}

/// <summary>Records that the viewer shared a post (share sheet or copied link), once per viewer per UK day.</summary>
public sealed record RecordShareCommand(Guid PostId, ViewerContext Viewer) : IRequest;

public sealed class RecordShareCommandHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    ICurrentUser caller,
    IOptions<ReelsOptions> options,
    TimeProvider clock)
    : IRequestHandler<RecordShareCommand>
{
    public async Task Handle(RecordShareCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        if (!post.IsPublic)
        {
            return;
        }

        var viewer = Viewers.Resolve(caller, request.Viewer, options.Value);
        var now = clock.GetUtcNow();
        await engagement.RecordShareAsync(PostShare.Create(post.Id, viewer.Key, UkCalendar.Today(now), now), cancellationToken);
    }
}

/// <summary>
/// How far a post reached, for its author (and moderators): unique viewers, viewers whose home area is within
/// <see cref="PostView.LocalKm"/> km of where it happened, reactions, visible comments and shares. Counts only.
/// </summary>
public sealed record PostReachDto(Guid PostId, int Views, int LocalViews, int Reactions, int Comments, int Shares);

/// <summary>A post's reach, for its author or a moderator.</summary>
public sealed record GetPostReachQuery(Guid PostId) : IRequest<PostReachDto>;

public sealed class GetPostReachQueryHandler(IPostRepository posts, IEngagementRepository engagement, ICurrentUser caller)
    : IRequestHandler<GetPostReachQuery, PostReachDto>
{
    public async Task<PostReachDto> Handle(GetPostReachQuery request, CancellationToken cancellationToken)
    {
        caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        if (!PostAccess.IsAuthor(post, caller) && !PostAccess.IsModerator(caller))
        {
            throw new ForbiddenException("Only the post's author can see its reach.");
        }

        var reach = await engagement.GetReachAsync(post.Id, cancellationToken);
        var stats = (await posts.GetStatsAsync([post.Id], null, cancellationToken)).GetValueOrDefault(post.Id) ?? PostStats.Empty;
        return new PostReachDto(post.Id, reach.Views, reach.LocalViews, stats.Reactions.Values.Sum(), stats.CommentCount, reach.Shares);
    }
}

/// <summary>How long each point of a post's analytics covers.</summary>
public enum AnalyticsInterval
{
    Hour,
    Day,
    Week,
}

/// <summary>One point of a post's analytics: what happened from <c>Start</c> until the next point.</summary>
public sealed record AnalyticsPointDto(DateTimeOffset Start, int Views, int LocalViews, int Reactions, int Comments, int Shares);

/// <summary>
/// A post's analytics (owner, 22 Oct 2026), for its author: views, local views, reactions, comments and shares over
/// time since it was posted, hourly for its first 48 hours, then daily, then weekly once it's over 90 days old.
/// Premium and Premium + only: on Free, <c>Locked</c> is true and there are no points (the app shows the graphs
/// blurred, with an upgrade prompt); <c>Totals</c> are always there, as on the reach panel. Moderators see everything.
/// </summary>
public sealed record PostAnalyticsDto(
    Guid PostId,
    bool Locked,
    AnalyticsInterval Interval,
    DateTimeOffset Since,
    IReadOnlyList<AnalyticsPointDto> Points,
    PostReachDto Totals);

/// <summary>A post's analytics, for its author or a moderator.</summary>
public sealed record GetPostAnalyticsQuery(Guid PostId) : IRequest<PostAnalyticsDto>;

public sealed class GetPostAnalyticsQueryHandler(
    IPostRepository posts,
    IEngagementRepository engagement,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<GetPostAnalyticsQuery, PostAnalyticsDto>
{
    /// <summary>Points are hourly while the post is this young.</summary>
    public static readonly TimeSpan HourlyFor = TimeSpan.FromHours(48);

    /// <summary>Then daily until it's this old, then weekly.</summary>
    public static readonly TimeSpan DailyFor = TimeSpan.FromDays(90);

    public async Task<PostAnalyticsDto> Handle(GetPostAnalyticsQuery request, CancellationToken cancellationToken)
    {
        caller.RequireUserId();
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken);
        var moderator = PostAccess.IsModerator(caller);
        if (!PostAccess.IsAuthor(post, caller) && !moderator)
        {
            throw new ForbiddenException("Only the post's author can see its analytics.");
        }

        var reach = await engagement.GetReachAsync(post.Id, cancellationToken);
        var stats = (await posts.GetStatsAsync([post.Id], null, cancellationToken)).GetValueOrDefault(post.Id) ?? PostStats.Empty;
        var totals = new PostReachDto(post.Id, reach.Views, reach.LocalViews, stats.Reactions.Values.Sum(), stats.CommentCount, reach.Shares);

        var now = clock.GetUtcNow();
        var interval = IntervalFor(now - post.CreatedAt);
        var locked = !moderator && plans.CurrentPlan() is not (SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus);
        if (locked)
        {
            return new PostAnalyticsDto(post.Id, true, interval, post.CreatedAt, [], totals);
        }

        var activity = await engagement.GetActivityAsync(post.Id, cancellationToken);
        return new PostAnalyticsDto(post.Id, false, interval, post.CreatedAt, Points(post.CreatedAt, now, interval, activity), totals);
    }

    public static AnalyticsInterval IntervalFor(TimeSpan age) =>
        age <= HourlyFor ? AnalyticsInterval.Hour : age <= DailyFor ? AnalyticsInterval.Day : AnalyticsInterval.Week;

    public static TimeSpan Length(AnalyticsInterval interval) => interval switch
    {
        AnalyticsInterval.Hour => TimeSpan.FromHours(1),
        AnalyticsInterval.Day => TimeSpan.FromDays(1),
        _ => TimeSpan.FromDays(7),
    };

    /// <summary>
    /// One point per interval from when it was posted up to now (the last one still filling up). Anything recorded
    /// before the post's own time (clock differences) counts in the first point.
    /// </summary>
    public static IReadOnlyList<AnalyticsPointDto> Points(DateTimeOffset since, DateTimeOffset now, AnalyticsInterval interval, PostActivity activity)
    {
        var length = Length(interval);
        var count = Math.Max(1, (int)Math.Ceiling((now - since) / length));
        var views = new int[count];
        var local = new int[count];
        var reactions = new int[count];
        var comments = new int[count];
        var shares = new int[count];
        int Index(DateTimeOffset at) => Math.Clamp((int)Math.Floor((at - since) / length), 0, count - 1);

        foreach (var (at, isLocal) in activity.Views)
        {
            var i = Index(at);
            views[i]++;
            if (isLocal)
            {
                local[i]++;
            }
        }

        foreach (var at in activity.Reactions)
        {
            reactions[Index(at)]++;
        }

        foreach (var at in activity.Comments)
        {
            comments[Index(at)]++;
        }

        foreach (var at in activity.Shares)
        {
            shares[Index(at)]++;
        }

        return Enumerable.Range(0, count)
            .Select(i => new AnalyticsPointDto(since + (length * i), views[i], local[i], reactions[i], comments[i], shares[i]))
            .ToList();
    }
}

/// <summary>The caller's settings for their own posts. <c>VoteDuration</c>: when votes on new posts close.</summary>
public sealed record AuthorPreferencesDto(VoteDuration VoteDuration);

public sealed record GetAuthorPreferencesQuery : IRequest<AuthorPreferencesDto>;

public sealed class GetAuthorPreferencesQueryHandler(IEngagementRepository engagement, ICurrentUser caller)
    : IRequestHandler<GetAuthorPreferencesQuery, AuthorPreferencesDto>
{
    public async Task<AuthorPreferencesDto> Handle(GetAuthorPreferencesQuery request, CancellationToken cancellationToken)
    {
        var found = await engagement.GetPreferencesAsync(caller.RequireUserId(), cancellationToken);
        return new AuthorPreferencesDto(found?.VoteDuration ?? VoteDuration.Days7);
    }
}

/// <summary>Changes when votes on the caller's new posts close. Posts already made keep their closing time.</summary>
public sealed record SetAuthorPreferencesCommand(VoteDuration VoteDuration) : IRequest<AuthorPreferencesDto>;

public sealed class SetAuthorPreferencesCommandValidator : AbstractValidator<SetAuthorPreferencesCommand>
{
    public SetAuthorPreferencesCommandValidator()
    {
        RuleFor(c => c.VoteDuration).IsInEnum();
    }
}

public sealed class SetAuthorPreferencesCommandHandler(IEngagementRepository engagement, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<SetAuthorPreferencesCommand, AuthorPreferencesDto>
{
    public async Task<AuthorPreferencesDto> Handle(SetAuthorPreferencesCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var now = clock.GetUtcNow();
        var preferences = await engagement.GetPreferencesAsync(userId, cancellationToken);
        if (preferences is null)
        {
            preferences = AuthorPreferences.Create(userId, now);
            engagement.AddPreferences(preferences);
        }

        preferences.SetVoteDuration(request.VoteDuration, now);
        await engagement.SaveChangesAsync(cancellationToken);
        return new AuthorPreferencesDto(preferences.VoteDuration);
    }
}

/// <summary>Who is looking: a member by their id, a signed-out visitor as for reels.</summary>
internal static class Viewers
{
    public static ReelViewer Resolve(ICurrentUser caller, ViewerContext context, ReelsOptions options) =>
        caller.UserId() is { } userId
            ? ReelViewer.ForUser(userId)
            : ReelViewer.ForAnonymous(context.ViewerId, context.IpAddress, options.ViewerKeySecret)
                ?? ReelViewer.ForAnonymous(null, "unknown", options.ViewerKeySecret)!;
}
