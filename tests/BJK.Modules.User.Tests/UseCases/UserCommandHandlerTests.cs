using BJK.Modules.Shared.Exceptions;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Reset;
using BJK.Modules.User.UseCases.Update;
using NSubstitute;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class UserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_saves_new_user_and_returns_its_id()
    {
        var handler = new AddUserCommandHandler(_users, _clock);

        var id = await handler.Handle(new AddUserCommand("sub-1", "rider@example.com", "Road Hog"), Ct);

        _users.Received(1).Add(Arg.Is<UserProfile>(u => u.Id == id && u.IdentityId == "sub-1"));
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_by_owner_changes_display_name()
    {
        var user = ExistingUser("sub-1");
        var handler = new UpdateUserCommandHandler(_users, _clock);

        await handler.Handle(new UpdateUserCommand(user.Id, "sub-1", "Lane Hogger"), Ct);

        Assert.Equal("Lane Hogger", user.DisplayName);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_by_someone_else_is_forbidden()
    {
        var user = ExistingUser("sub-1");
        var handler = new UpdateUserCommandHandler(_users, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new UpdateUserCommand(user.Id, "sub-2", "Hijacked"), Ct));

        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_of_missing_user_is_not_found()
    {
        var handler = new UpdateUserCommandHandler(_users, _clock);

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.Handle(new UpdateUserCommand(Guid.NewGuid(), "sub-1", "Anyone"), Ct));
    }

    [Fact]
    public async Task Delete_by_owner_soft_deletes()
    {
        var user = ExistingUser("sub-1");
        var handler = new DeleteUserCommandHandler(_users, _clock);

        await handler.Handle(new DeleteUserCommand(user.Id, "sub-1"), Ct);

        Assert.True(user.IsDeleted);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_asks_identity_server_for_owners_account()
    {
        var user = ExistingUser("sub-1");
        var handler = new ResetUserPasswordCommandHandler(_users, _identity);

        await handler.Handle(new ResetUserPasswordCommand(user.Id, "sub-1"), Ct);

        await _identity.Received(1).SendPasswordResetAsync("sub-1", Arg.Any<CancellationToken>());
    }

    private UserProfile ExistingUser(string identityId)
    {
        var user = UserProfile.Create(identityId, "rider@example.com", "Road Hog", Now);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }
}
