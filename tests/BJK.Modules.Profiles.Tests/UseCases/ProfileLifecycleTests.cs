using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Profiles.UseCases.Get;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Users;
using NSubstitute;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.UseCases;

public sealed class ProfileLifecycleTests
{
    private readonly IProfileRepository _profiles = Substitute.For<IProfileRepository>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly Profile _profile;
    private readonly ProfilesUserLifecycleHandler _handler;

    public ProfileLifecycleTests()
    {
        _profile = Profile.Create(UserId, "roadhog", "Road Hog", null, null, Now);
        _profiles.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(_profile);
        _profiles.GetByHandleAsync("roadhog", Arg.Any<CancellationToken>()).Returns(_profile);
        _profiles.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Profile> { _profile });
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaSummary>());
        _handler = new ProfilesUserLifecycleHandler(_profiles, new FixedTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserLifecycleEvent Event(UserLifecycleEventType type) => new(Guid.NewGuid(), UserId, type, Now);

    [Fact]
    public async Task A_deactivated_users_profile_disappears_and_comes_back()
    {
        var query = new GetProfileQueryHandler(_profiles, _media);
        var directory = new ProfileDirectory(_profiles, _media);

        await _handler.HandleAsync(Event(UserLifecycleEventType.Deactivated), Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => query.Handle(new GetProfileQuery("roadhog"), Ct));
        Assert.False(await directory.ExistsAsync(UserId, Ct));
        Assert.Empty(await directory.GetSummariesAsync([UserId], Ct));
        Assert.Null(await directory.FindByHandleAsync("roadhog", Ct));

        await _handler.HandleAsync(Event(UserLifecycleEventType.Reactivated), Ct);

        Assert.Equal("roadhog", (await query.Handle(new GetProfileQuery("roadhog"), Ct)).Handle);
        Assert.True(await directory.ExistsAsync(UserId, Ct));
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_profile()
    {
        await _handler.HandleAsync(Event(UserLifecycleEventType.Deleted), Ct);

        _profiles.Received(1).Remove(_profile);
        await _profiles.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Events_for_users_without_a_profile_are_ignored()
    {
        await _handler.HandleAsync(new UserLifecycleEvent(Guid.NewGuid(), OtherUserId, UserLifecycleEventType.Deleted, Now), Ct);

        await _profiles.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
