using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Reels;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class ReelsTests
{
    private readonly IReelRepository _reels = Substitute.For<IReelRepository>();
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly ViewerContext _viewer = new("web-viewer-0123456789", "203.0.113.7");

    public ReelsTests()
    {
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary>());
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
        _blocks.GetBlockedEitherWayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _reels.ListAsync(Arg.Any<ReelSearch>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Enumerable.Range(0, call.ArgAt<int>(1)).Select(i => Video(Now.AddMinutes(-i))).ToList());
        _reels.RecordPlayAsync(Arg.Any<ReelPlay>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Post Video(DateTimeOffset createdAt, bool sensitive = false) =>
        Post.Create(OtherUserId, Details(), [Guid.NewGuid()], createdAt, isSensitive: sensitive, hasVideo: true);

    private void SignedIn(SubscriptionPlan plan, bool ageVerified = false)
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _caller.AgeVerified.Returns(ageVerified);
        _plans.CurrentPlan().Returns(plan);
    }

    private void PlaysToday(int count) =>
        _reels.CountPlaysAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(count);

    private ReelAllowances Allowances() =>
        new(_reels, _plans, _caller, Options.Create(new ReelsOptions()), _clock);

    private ListReelsQueryHandler ListHandler() =>
        new(_reels, Allowances(), _blocks, new PostViews(_posts, _profiles, _media, _caller), _caller);

    private RecordReelPlayCommandHandler PlayHandler() =>
        new(_posts, _reels, Allowances(), _blocks, _caller, _clock);

    [Fact]
    public async Task Logged_out_viewers_get_10_a_day_counted_by_their_viewer_id()
    {
        PlaysToday(4);

        var page = await ListHandler().Handle(new ListReelsQuery(null, null, _viewer), Ct);

        Assert.Equal(6, page.PlaysRemainingToday);
        Assert.Equal(10, page.DailyPlayLimit);
        Assert.Equal(4, page.PlaysToday);
        Assert.Equal(6, page.Items.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 23, 0, 0, TimeSpan.Zero), page.ResetsAt);

        var expectedKey = ReelViewer.ForAnonymous(_viewer.ViewerId, null, null)!.Key;
        await _reels.Received().CountPlaysAsync(expectedKey, new DateOnly(2026, 10, 18), Arg.Any<CancellationToken>());
        await _reels.Received().ListAsync(
            Arg.Is<ReelSearch>(s => !s.IncludeSensitive && s.ViewerId == null),
            7,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Free_users_get_25_and_premium_is_unlimited()
    {
        SignedIn(SubscriptionPlan.Free);
        PlaysToday(18);
        var free = await ListHandler().Handle(new ListReelsQuery(null, null, _viewer), Ct);
        Assert.Equal(7, free.PlaysRemainingToday);
        Assert.Equal(25, free.DailyPlayLimit);
        await _reels.Received().CountPlaysAsync(UserId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());

        SignedIn(SubscriptionPlan.Premium);
        PlaysToday(400);
        var premium = await ListHandler().Handle(new ListReelsQuery(null, null, _viewer, PageSize: 5), Ct);
        Assert.Null(premium.PlaysRemainingToday);
        Assert.Null(premium.DailyPlayLimit);
        Assert.Equal(5, premium.Items.Count);
        Assert.NotNull(premium.NextCursor);
    }

    [Fact]
    public async Task Listing_with_nothing_left_is_a_403_daily_limit()
    {
        SignedIn(SubscriptionPlan.Free);
        PlaysToday(25);

        var ex = await Assert.ThrowsAsync<DailyLimitReachedException>(
            () => ListHandler().Handle(new ListReelsQuery(null, null, _viewer), Ct));
        Assert.Equal(25, ex.Limit);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 23, 0, 0, TimeSpan.Zero), ex.ResetsAt);
    }

    [Fact]
    public async Task Sensitive_reels_are_included_only_for_age_verified_viewers_and_blocks_are_left_out()
    {
        SignedIn(SubscriptionPlan.Free, ageVerified: true);
        var blocked = new List<Guid> { OtherUserId };
        _blocks.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(blocked);

        await ListHandler().Handle(new ListReelsQuery(null, IncidentCategory.Tailgating, _viewer), Ct);

        await _reels.Received().ListAsync(
            Arg.Is<ReelSearch>(s => s.IncludeSensitive && s.ViewerId == UserId && s.Category == IncidentCategory.Tailgating
                && s.ExcludeAuthorIds!.Contains(OtherUserId)),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_play_counts_once_and_replays_at_the_limit_are_free()
    {
        SignedIn(SubscriptionPlan.Free);
        var post = Video(Now.AddHours(-1));
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);

        PlaysToday(3);
        var first = await PlayHandler().Handle(new RecordReelPlayCommand(post.Id, _viewer), Ct);
        Assert.True(first.Counted);
        Assert.Equal(21, first.PlaysRemainingToday);
        await _reels.Received().RecordPlayAsync(
            Arg.Is<ReelPlay>(p => p.ViewerKey == UserId && !p.IsAnonymous && p.PostId == post.Id && p.Day == new DateOnly(2026, 10, 18)),
            Arg.Any<CancellationToken>());

        PlaysToday(25);
        _reels.HasPlayedAsync(UserId, Arg.Any<DateOnly>(), post.Id, Arg.Any<CancellationToken>()).Returns(true);
        var replay = await PlayHandler().Handle(new RecordReelPlayCommand(post.Id, _viewer), Ct);
        Assert.False(replay.Counted);
        Assert.Equal(0, replay.PlaysRemainingToday);

        var other = Video(Now.AddHours(-2));
        _posts.GetAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);
        await Assert.ThrowsAsync<DailyLimitReachedException>(
            () => PlayHandler().Handle(new RecordReelPlayCommand(other.Id, _viewer), Ct));
    }

    [Fact]
    public async Task Photo_posts_and_sensitive_reels_for_unverified_viewers_are_not_reels()
    {
        SignedIn(SubscriptionPlan.Free);
        var photos = Post.Create(OtherUserId, Details(), [Guid.NewGuid(), Guid.NewGuid()], Now);
        var sensitive = Video(Now, sensitive: true);
        _posts.GetAsync(photos.Id, Arg.Any<CancellationToken>()).Returns(photos);
        _posts.GetAsync(sensitive.Id, Arg.Any<CancellationToken>()).Returns(sensitive);

        await Assert.ThrowsAsync<NotFoundException>(() => PlayHandler().Handle(new RecordReelPlayCommand(photos.Id, _viewer), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => PlayHandler().Handle(new RecordReelPlayCommand(sensitive.Id, _viewer), Ct));
        await _reels.DidNotReceive().RecordPlayAsync(Arg.Any<ReelPlay>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Ranking_favours_recent_posts_but_engagement_keeps_a_clip_up()
    {
        var fresh = ReelRanking.Score(0, 0, TimeSpan.FromHours(1));
        var dayOld = ReelRanking.Score(0, 0, TimeSpan.FromHours(24));
        var popularDayOld = ReelRanking.Score(40, 10, TimeSpan.FromHours(24));
        Assert.True(fresh > dayOld);
        Assert.True(popularDayOld > fresh);
        Assert.Equal(1 / Math.Pow(2, 1.5), Post.Create(UserId, Details(), [Guid.NewGuid()], Now, hasVideo: true).ReelScore, 10);
        Assert.Equal(0, Post.Create(UserId, Details(), [Guid.NewGuid()], Now).ReelScore);
    }

    [Fact]
    public void Cursors_round_trip_and_bad_ones_are_rejected()
    {
        var post = Video(new DateTimeOffset(2026, 10, 18, 8, 15, 30, 123, TimeSpan.Zero));
        Assert.True(ReelCursors.TryDecode(ReelCursors.Encode(post), out var cursor));
        Assert.Equal(post.ReelScore, cursor!.Score);
        Assert.Equal(post.CreatedAt, cursor.CreatedAt);
        Assert.False(ReelCursors.TryDecode("not a cursor!", out _));
        Assert.True(ReelCursors.TryDecode(null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Viewer_keys_are_hashed_and_fall_back_to_the_ip_address()
    {
        var byId = ReelViewer.ForAnonymous("web-viewer-0123456789", "203.0.113.7", null)!;
        var sameId = ReelViewer.ForAnonymous("web-viewer-0123456789", "198.51.100.1", null)!;
        var byIp = ReelViewer.ForAnonymous("bad id!", "203.0.113.7", null)!;
        var keyed = ReelViewer.ForAnonymous("web-viewer-0123456789", null, "secret")!;

        Assert.True(byId.IsAnonymous);
        Assert.Equal(byId.Key, sameId.Key);
        Assert.NotEqual(byId.Key, byIp.Key);
        Assert.NotEqual(byId.Key, keyed.Key);
        Assert.Null(ReelViewer.ForAnonymous(null, null, null));
        Assert.False(ReelViewer.IsValidViewerId("short"));
        Assert.True(ReelViewer.IsValidViewerId("2f1c9a0e-6b1c-4d3e-8f00-000000000001"));
    }

    [Fact]
    public void Uk_days_follow_british_summer_time()
    {
        // 23:30 UTC on 1 July is 00:30 on 2 July in the UK (BST); in January UTC and UK time agree.
        Assert.Equal(new DateOnly(2026, 7, 2), UkCalendar.Today(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 1, 1), UkCalendar.Today(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 23, 0, 0, TimeSpan.Zero), UkCalendar.NextMidnight(new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero)));

        // The clocks go back on 25 October 2026: that night midnight is still BST (+1).
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(1)), UkCalendar.StartOf(new DateOnly(2026, 10, 25)));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), UkCalendar.StartOf(new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void Plan_claims_are_read_and_anything_unknown_is_free()
    {
        Assert.Equal(SubscriptionPlan.Premium, SubscriptionPlanClaims.Parse("premium"));
        Assert.Equal(SubscriptionPlan.PremiumPlus, SubscriptionPlanClaims.Parse("PREMIUM_PLUS"));
        Assert.Equal(SubscriptionPlan.Free, SubscriptionPlanClaims.Parse(null));
        Assert.Equal(SubscriptionPlan.Free, SubscriptionPlanClaims.Parse("gold"));
    }
}
