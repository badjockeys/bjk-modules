using System.Buffers.Text;
using System.Globalization;
using System.Text;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Reels;

/// <summary>
/// Who is watching, as the API sees them: the <c>X-Viewer-Id</c> header (logged-out viewers) and IP address. Signed-in
/// viewers are identified by their token instead.
/// </summary>
/// <param name="ViewerId">The <c>X-Viewer-Id</c> header, if any.</param>
/// <param name="IpAddress">The caller's IP address, if known.</param>
public sealed record ViewerContext(string? ViewerId, string? IpAddress);

/// <summary>
/// A viewer's reel plays today (UK calendar day). <c>Limit</c> and <c>Remaining</c> are null when unlimited (Premium
/// and Premium +).
/// </summary>
public sealed record ReelAllowance(ReelViewer Viewer, DateOnly Day, int? Limit, int Used, DateTimeOffset ResetsAt, SubscriptionPlan? Plan)
{
    /// <summary>Plays left today, or null for unlimited.</summary>
    public int? Remaining => Limit is { } limit ? Math.Max(0, limit - Used) : null;

    /// <summary>The 403 the apps turn into the "That's today's free reels" sheet.</summary>
    public DailyLimitReachedException LimitReached() => new(
        Plan is null
            ? $"That's today's {Limit} free reels. Join for free to keep watching."
            : $"That's today's {Limit} free reels. Come back tomorrow, or go unlimited with Premium.",
        Limit ?? 0,
        ResetsAt);
}

/// <summary>
/// Works out the caller's daily reel allowance: logged out <c>Reels:DailyPlays:Anonymous</c> (10), Free
/// <c>Reels:DailyPlays:Free</c> (25), Premium and Premium + unlimited. Plans come from the token's <c>plan</c> claim
/// (or <c>Plans:Overrides</c>); days are UK calendar days.
/// </summary>
public sealed class ReelAllowances(
    IReelRepository reels,
    IPlanResolver plans,
    ICurrentUser caller,
    IOptions<ReelsOptions> options,
    TimeProvider clock)
{
    public async Task<ReelAllowance> GetAsync(ViewerContext context, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var day = UkCalendar.Today(now);
        var plan = caller.UserId() is null ? (SubscriptionPlan?)null : plans.CurrentPlan() ?? SubscriptionPlan.Free;
        var viewer = caller.UserId() is { } userId
            ? ReelViewer.ForUser(userId)
            : ReelViewer.ForAnonymous(context.ViewerId, context.IpAddress, settings.ViewerKeySecret)
                ?? ReelViewer.ForAnonymous(null, "unknown", settings.ViewerKeySecret)!;
        int? limit = plan switch
        {
            null => settings.DailyPlays.Anonymous,
            SubscriptionPlan.Free => settings.DailyPlays.Free,
            _ => null,
        };
        var used = await reels.CountPlaysAsync(viewer.Key, day, cancellationToken);
        return new ReelAllowance(viewer, day, limit, used, UkCalendar.NextMidnight(now), plan);
    }
}

/// <summary>
/// A page of reels plus the caller's allowance. <c>PlaysRemainingToday</c> and <c>DailyPlayLimit</c> are null for
/// unlimited plans. <c>NextCursor</c> is null at the end. Rankings are refreshed every few minutes, so a later page can
/// occasionally repeat a reel: the apps skip ids they already have.
/// </summary>
public sealed record ReelsPage(
    IReadOnlyList<PostDto> Items,
    string? NextCursor,
    int? PlaysRemainingToday,
    int? DailyPlayLimit,
    int PlaysToday,
    DateTimeOffset ResetsAt);

/// <summary>
/// Reels: a full-screen feed of public video posts (one post = one reel), ranked recent and popular
/// (<see cref="ReelRanking"/>), optionally one category. Open to everyone; the daily allowance depends on the plan
/// (see <see cref="ReelAllowances"/>). When it's used up this fails with 403 <c>daily_limit</c> (and
/// <c>resetsAt</c>); otherwise a page is at most the plays left. Sensitive posts are left out unless the caller is
/// age-verified (or a moderator); authors still get their own. Posts by people the caller blocked, or who blocked the
/// caller, are left out. Items are the same <see cref="PostDto"/> as the feed.
/// </summary>
public sealed record ListReelsQuery(
    string? Cursor,
    IncidentCategory? Category,
    ViewerContext Viewer,
    int PageSize = 10,
    IncidentGroup? Group = null) : IRequest<ReelsPage>;

public sealed class ListReelsQueryValidator : AbstractValidator<ListReelsQuery>
{
    public ListReelsQueryValidator()
    {
        RuleFor(q => q.PageSize).InclusiveBetween(1, 20);
        RuleFor(q => q.Category).IsInEnum();
        RuleFor(q => q.Group).IsInEnum();
        RuleFor(q => q.Cursor)
            .Must(c => c is null || ReelCursors.TryDecode(c, out _))
            .WithMessage("The cursor isn't valid. Start again without one.");
    }
}

public sealed class ListReelsQueryHandler(
    IReelRepository reels,
    ReelAllowances allowances,
    IBlockDirectory blocks,
    PostViews views,
    ICurrentUser caller)
    : IRequestHandler<ListReelsQuery, ReelsPage>
{
    public async Task<ReelsPage> Handle(ListReelsQuery request, CancellationToken cancellationToken)
    {
        var allowance = await allowances.GetAsync(request.Viewer, cancellationToken);
        if (allowance.Remaining is 0)
        {
            throw allowance.LimitReached();
        }

        var viewerId = caller.UserId();
        var excluded = viewerId is { } id ? await blocks.GetBlockedEitherWayAsync(id, cancellationToken) : null;
        var take = Math.Min(request.PageSize, allowance.Remaining ?? request.PageSize);
        ReelCursors.TryDecode(request.Cursor, out var after);

        var items = await reels.ListAsync(
            new ReelSearch(
                request.Category,
                IncludeSensitive: caller.AgeVerified || PostAccess.IsModerator(caller),
                viewerId,
                excluded,
                after,
                request.Group),
            take + 1,
            cancellationToken);
        var page = items.Take(take).ToList();
        var next = items.Count > take && page.Count > 0 ? ReelCursors.Encode(page[^1]) : null;

        return new ReelsPage(
            await views.BuildAsync(page, viewerId, cancellationToken),
            next,
            allowance.Remaining,
            allowance.Limit,
            allowance.Used,
            allowance.ResetsAt);
    }
}

/// <summary>
/// Whether a play counted (false if the viewer already played this reel today, which is free), and the allowance
/// afterwards.
/// </summary>
public sealed record ReelPlayResult(bool Counted, int? PlaysRemainingToday, int? DailyPlayLimit, int PlaysToday, DateTimeOffset ResetsAt);

/// <summary>
/// The app reports a reel play once the viewer has watched about 3 seconds. Idempotent per viewer, post and UK day:
/// repeats change nothing. Counts towards the daily allowance; when it's used up, a play of a reel not already played
/// today fails with 403 <c>daily_limit</c>. The post must be a public video post the caller can see in reels.
/// </summary>
public sealed record RecordReelPlayCommand(Guid PostId, ViewerContext Viewer) : IRequest<ReelPlayResult>;

public sealed class RecordReelPlayCommandHandler(
    IPostRepository posts,
    IReelRepository reels,
    ReelAllowances allowances,
    IBlockDirectory blocks,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<RecordReelPlayCommand, ReelPlayResult>
{
    public async Task<ReelPlayResult> Handle(RecordReelPlayCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        var viewerId = caller.UserId();
        var mayViewSensitive = caller.AgeVerified || PostAccess.IsModerator(caller) || (viewerId is { } v && post.IsOwnedBy(v));
        if (!post.HasVideo || (post.IsSensitive && !mayViewSensitive))
        {
            throw new NotFoundException("Reel", request.PostId);
        }

        if (viewerId is { } id && id != post.AuthorId)
        {
            var blocked = await blocks.GetBlockedEitherWayAsync(id, cancellationToken);
            if (blocked.Contains(post.AuthorId))
            {
                throw new NotFoundException("Reel", request.PostId);
            }
        }

        var allowance = await allowances.GetAsync(request.Viewer, cancellationToken);
        if (allowance.Remaining is 0)
        {
            // Watching one of today's reels again is free; a new one isn't.
            if (await reels.HasPlayedAsync(allowance.Viewer.Key, allowance.Day, post.Id, cancellationToken))
            {
                return Result(allowance, counted: false, allowance.Used);
            }

            throw allowance.LimitReached();
        }

        var counted = await reels.RecordPlayAsync(
            new ReelPlay(allowance.Viewer, allowance.Day, post.Id, clock.GetUtcNow()),
            cancellationToken);
        return Result(allowance, counted, counted ? allowance.Used + 1 : allowance.Used);
    }

    private static ReelPlayResult Result(ReelAllowance allowance, bool counted, int used) => new(
        counted,
        allowance.Limit is { } limit ? Math.Max(0, limit - used) : null,
        allowance.Limit,
        used,
        allowance.ResetsAt);
}

/// <summary>Opaque reels cursors: the last reel's score and creation time, base64url.</summary>
public static class ReelCursors
{
    public static string Encode(Post post) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            post.ReelScore.ToString("R", CultureInfo.InvariantCulture) + "|" + post.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecode(string? value, out ReelCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value.Trim())).Split('|');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                && double.IsFinite(score)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.UtcTicks
                && ticks <= DateTimeOffset.MaxValue.UtcTicks)
            {
                cursor = new ReelCursor(score, new DateTimeOffset(ticks, TimeSpan.Zero));
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
