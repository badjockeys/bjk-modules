using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Engagement;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Post analytics (owner, 22 Oct 2026): over time since posting, Premium only.</summary>
public sealed class PostAnalyticsTests
{
    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IEngagementRepository _engagement = Substitute.For<IEngagementRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();

    public PostAnalyticsTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        _engagement.GetReachAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new PostReach(3, 1, 1));
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, AnalyticsInterval.Hour)]
    [InlineData(48, AnalyticsInterval.Hour)]
    [InlineData(49, AnalyticsInterval.Day)]
    [InlineData(24 * 90, AnalyticsInterval.Day)]
    [InlineData((24 * 90) + 1, AnalyticsInterval.Week)]
    public void The_interval_grows_with_the_posts_age(int hours, AnalyticsInterval interval) =>
        Assert.Equal(interval, GetPostAnalyticsQueryHandler.IntervalFor(TimeSpan.FromHours(hours)));

    [Fact]
    public void Each_thing_counts_in_the_hour_it_happened()
    {
        var activity = new PostActivity(
            [(Now.AddMinutes(10), true), (Now.AddMinutes(70), false), (Now.AddMinutes(-5), false)],
            [Now.AddMinutes(65)],
            [Now.AddMinutes(130)],
            [Now.AddMinutes(30)]);

        var points = GetPostAnalyticsQueryHandler.Points(Now, Now.AddMinutes(150), AnalyticsInterval.Hour, activity);

        Assert.Equal(3, points.Count);
        Assert.Equal(Now.AddHours(2), points[2].Start);
        Assert.Equal(new[] { 2, 1, 0 }, points.Select(p => p.Views));
        Assert.Equal(new[] { 1, 0, 0 }, points.Select(p => p.LocalViews));
        Assert.Equal(new[] { 0, 1, 0 }, points.Select(p => p.Reactions));
        Assert.Equal(new[] { 0, 0, 1 }, points.Select(p => p.Comments));
        Assert.Equal(new[] { 1, 0, 0 }, points.Select(p => p.Shares));
    }

    [Fact]
    public async Task Premium_authors_get_the_points()
    {
        var post = Existing(UserId);
        _engagement.GetActivityAsync(post.Id, Arg.Any<CancellationToken>()).Returns(new PostActivity([(Now.AddMinutes(5), false)], [], [], []));

        var result = await Handler(Now.AddHours(3)).Handle(new GetPostAnalyticsQuery(post.Id), Ct);

        Assert.False(result.Locked);
        Assert.Equal(AnalyticsInterval.Hour, result.Interval);
        Assert.Equal(3, result.Points.Count);
        Assert.Equal(3, result.Totals.Views);
    }

    [Fact]
    public async Task Free_authors_see_totals_but_the_graphs_are_locked()
    {
        var post = Existing(UserId);
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);

        var result = await Handler(Now.AddDays(3)).Handle(new GetPostAnalyticsQuery(post.Id), Ct);

        Assert.True(result.Locked);
        Assert.Empty(result.Points);
        Assert.Equal(1, result.Totals.Shares);
        await _engagement.DidNotReceiveWithAnyArgs().GetActivityAsync(default, Ct);
    }

    [Fact]
    public async Task Only_the_author_sees_them()
    {
        var post = Existing(OtherUserId);

        await Assert.ThrowsAsync<ForbiddenException>(() => Handler(Now.AddHours(1)).Handle(new GetPostAnalyticsQuery(post.Id), Ct));
    }

    private GetPostAnalyticsQueryHandler Handler(DateTimeOffset now) => new(_posts, _engagement, _plans, _caller, new FixedTimeProvider(now));

    private Post Existing(Guid authorId)
    {
        var post = NewPost(authorId);
        _posts.GetAsync(post.Id, Arg.Any<CancellationToken>()).Returns(post);
        return post;
    }
}
