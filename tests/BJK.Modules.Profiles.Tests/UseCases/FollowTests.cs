using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Profiles.UseCases.Follows;
using BJK.Modules.Profiles.UseCases.Get;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

public sealed class FollowTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly INotificationSender _notifications = Substitute.For<INotificationSender>();
    private readonly IHomeAreas _homeAreas = Substitute.For<IHomeAreas>();
    private readonly IActivePosters _posters = Substitute.For<IActivePosters>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly Profile _me = Profile.Create(UserId, "roadhog", "Road Hog", null, null, Now);
    private readonly Profile _other = Profile.Create(OtherUserId, "speedy", "Speedy", "Drives fast", "Bury", Now);

    public FollowTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _profiles.GetByHandleAsync("speedy", Arg.Any<CancellationToken>()).Returns(_other);
        _profiles.GetByIdAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns(_other);
        _profiles.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(_me);
        _profiles.GetFollowingIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FollowUserCommandHandler FollowHandler() => new(_profiles, _caller, _clock, _notifications);

    [Fact]
    public async Task Following_someone_records_it_and_tells_them_once()
    {
        _profiles.CountFollowsAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns((1, 0));

        var state = await FollowHandler().Handle(new FollowUserCommand("@Speedy"), Ct);

        Assert.True(state.Following);
        Assert.Equal(1, state.FollowerCount);
        _profiles.Received(1).AddFollow(Arg.Is<Follow>(f => f.FollowerId == UserId && f.FolloweeId == OtherUserId));
        await _notifications.Received(1).NotifyAsync(
            Arg.Is<NotificationRequest>(n => n.UserId == OtherUserId && n.Type == NotificationType.NewFollower && n.ActorId == UserId),
            Arg.Any<CancellationToken>());

        _profiles.GetFollowAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(Follow.Create(UserId, OtherUserId, Now));
        _notifications.ClearReceivedCalls();
        await FollowHandler().Handle(new FollowUserCommand("speedy"), Ct);
        await _notifications.DidNotReceive().NotifyAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task You_cant_follow_yourself_across_a_block_or_without_a_profile()
    {
        _profiles.GetByHandleAsync("roadhog", Arg.Any<CancellationToken>()).Returns(_me);
        await Assert.ThrowsAsync<ValidationException>(() => FollowHandler().Handle(new FollowUserCommand("roadhog"), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => FollowHandler().Handle(new FollowUserCommand("nobody_here"), Ct));

        _profiles.IsBlockedAsync(OtherUserId, UserId, Arg.Any<CancellationToken>()).Returns(true);
        await Assert.ThrowsAsync<ValidationException>(() => FollowHandler().Handle(new FollowUserCommand("speedy"), Ct));

        _profiles.IsBlockedAsync(OtherUserId, UserId, Arg.Any<CancellationToken>()).Returns(false);
        _profiles.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((Profile?)null);
        await Assert.ThrowsAsync<ValidationException>(() => FollowHandler().Handle(new FollowUserCommand("speedy"), Ct));
        _profiles.DidNotReceive().AddFollow(Arg.Any<Follow>());
        Assert.Throws<ArgumentException>(() => Follow.Create(UserId, UserId, Now));
    }

    [Fact]
    public async Task Unfollowing_removes_the_follow_and_blocking_ends_follows_either_way()
    {
        var follow = Follow.Create(UserId, OtherUserId, Now);
        _profiles.GetFollowAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(follow);

        var state = await new UnfollowUserCommandHandler(_profiles, _caller).Handle(new UnfollowUserCommand("speedy"), Ct);

        Assert.False(state.Following);
        _profiles.Received(1).RemoveFollow(follow);

        await new BlockUserCommandHandler(_profiles, _media, _caller, _clock).Handle(new BlockUserCommand("speedy"), Ct);
        await _profiles.Received(1).RemoveFollowsBetweenAsync(UserId, OtherUserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Profiles_show_follow_counts_and_whether_you_follow_each_other()
    {
        _profiles.CountFollowsAsync(OtherUserId, Arg.Any<CancellationToken>()).Returns((12, 3));
        _profiles.GetFollowAsync(UserId, OtherUserId, Arg.Any<CancellationToken>()).Returns(Follow.Create(UserId, OtherUserId, Now));

        var seen = await new GetProfileQueryHandler(_profiles, _media, _caller).Handle(new GetProfileQuery("speedy"), Ct);

        Assert.Equal(12, seen.FollowerCount);
        Assert.Equal(3, seen.FollowingCount);
        Assert.True(seen.IsFollowing);
        Assert.False(seen.FollowsYou);

        var anonymous = await new GetProfileQueryHandler(_profiles, _media).Handle(new GetProfileQuery("speedy"), Ct);
        Assert.Null(anonymous.IsFollowing);
        Assert.Equal(12, anonymous.FollowerCount);
    }

    [Fact]
    public async Task Who_to_follow_suggests_the_most_active_nearby_posters_you_dont_follow_or_block()
    {
        var busy = Guid.NewGuid();
        var popular = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        var minor = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var followed = Guid.NewGuid();
        _homeAreas.GetAsync(UserId, Arg.Any<CancellationToken>()).Returns(new HomeArea("Prescot", 53.43m, -2.80m, 5));
        _posters.MostActiveAsync(53.43m, -2.80m, 25, Now - WhoToFollowQueryHandler.Period, 200, Arg.Any<CancellationToken>())
            .Returns(new List<ActivePoster>
            {
                new(UserId, 9, 0), new(followed, 8, 0), new(blocked, 7, 0), new(minor, 6, 0),
                new(busy, 5, 0), new(quiet, 1, 0), new(popular, 3, 400),
            });
        _profiles.GetFollowingIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { followed });
        _profiles.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { blocked });
        _profiles.CountFollowedByAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [quiet] = 3 });
        Suggestable((busy, "busy"), (popular, "popular"), (quiet, "quiet"));

        var result = await WhoToFollow().Handle(new WhoToFollowQuery(Limit: 3), Ct);

        // busy 50; popular 30 + 50 for engagement = 80; quiet 10 + 45 (three of your follows follow them) = 55.
        Assert.True(result.HasHomeArea);
        Assert.Equal(new[] { "popular", "quiet", "busy" }, result.People.Select(p => p.Handle));
        await _profiles.Received().GetSuggestableAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => !ids.Contains(UserId) && !ids.Contains(followed) && !ids.Contains(blocked)),
            Arg.Any<CancellationToken>());

        _caller.IsMinor.Returns(true);
        Assert.Empty((await WhoToFollow().Handle(new WhoToFollowQuery(), Ct)).People);
    }

    [Fact]
    public async Task Without_a_home_area_it_uses_where_you_post_then_fills_from_the_whole_UK()
    {
        var local = Guid.NewGuid();
        var national = Guid.NewGuid();
        _homeAreas.GetAsync(UserId, Arg.Any<CancellationToken>()).Returns((HomeArea?)null);
        _posters.UsualPlaceAsync(UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns((53.48m, -2.24m));
        _posters.MostActiveAsync(53.48m, -2.24m, 25, Arg.Any<DateTimeOffset>(), 200, Arg.Any<CancellationToken>())
            .Returns(new List<ActivePoster> { new(local, 2, 0) });
        _posters.MostActiveAsync(null, null, Arg.Any<int>(), Arg.Any<DateTimeOffset>(), 200, Arg.Any<CancellationToken>())
            .Returns(new List<ActivePoster> { new(local, 2, 0), new(national, 40, 900) });
        _profiles.CountFollowedByAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        Suggestable((local, "local"), (national, "national"));

        var result = await WhoToFollow().Handle(new WhoToFollowQuery(Limit: 3), Ct);

        Assert.False(result.HasHomeArea);
        Assert.Equal(new[] { "local", "national" }, result.People.Select(p => p.Handle));
    }

    [Fact]
    public void Posting_engagement_and_your_follows_all_count_but_engagement_and_follows_are_capped() =>
        Assert.Equal(20 + 50 + 45, WhoToFollowQueryHandler.Score(new ActivePoster(Guid.NewGuid(), 2, 10_000), followedByYourFollows: 9));

    private WhoToFollowQueryHandler WhoToFollow() => new(_profiles, _media, _homeAreas, _posters, _caller, _clock);

    /// <summary>These people are active adults (anyone else asked about isn't).</summary>
    private void Suggestable(params (Guid Id, string Handle)[] people)
    {
        var known = people.ToDictionary(p => p.Id, p => p.Handle);
        _profiles.GetSuggestableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(call =>
            (IReadOnlyList<Profile>)call.Arg<IReadOnlyCollection<Guid>>()
                .Where(known.ContainsKey)
                .Select(id => Profile.Create(id, known[id], known[id], null, null, Now))
                .ToList());
    }
}
