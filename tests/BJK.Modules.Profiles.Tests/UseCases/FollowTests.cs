using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Blocks;
using BJK.Modules.Profiles.UseCases.Follows;
using BJK.Modules.Profiles.UseCases.Get;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
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
    public async Task Who_to_follow_suggests_nearby_adults_you_dont_follow_or_block()
    {
        var near1 = Guid.NewGuid();
        var near2 = Guid.NewGuid();
        var minor = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var followed = Guid.NewGuid();
        _homeAreas.GetAsync(UserId, Arg.Any<CancellationToken>()).Returns(new HomeArea("Prescot", 53.43m, -2.80m, 5));
        _homeAreas.UsersNearAsync(53.43m, -2.80m, 25, 200, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { UserId, followed, blocked, minor, near2, near1 });
        _profiles.GetFollowingIdsAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { followed });
        _profiles.GetBlockedEitherWayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Guid> { blocked });
        _profiles.GetSuggestableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Profile>
        {
            Profile.Create(near1, "near_one", "Near One", null, null, Now),
            Profile.Create(near2, "near_two", "Near Two", null, null, Now),
        });
        var handler = new WhoToFollowQueryHandler(_profiles, _media, _homeAreas, _caller);

        var result = await handler.Handle(new WhoToFollowQuery(), Ct);

        Assert.True(result.HasHomeArea);
        Assert.Equal(new[] { "near_two", "near_one" }, result.People.Select(p => p.Handle));
        await _profiles.Received().GetSuggestableAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => !ids.Contains(UserId) && !ids.Contains(followed) && !ids.Contains(blocked)),
            Arg.Any<CancellationToken>());

        _caller.IsMinor.Returns(true);
        Assert.Empty((await handler.Handle(new WhoToFollowQuery(), Ct)).People);

        _homeAreas.GetAsync(UserId, Arg.Any<CancellationToken>()).Returns((HomeArea?)null);
        Assert.False((await handler.Handle(new WhoToFollowQuery(), Ct)).HasHomeArea);
    }
}
