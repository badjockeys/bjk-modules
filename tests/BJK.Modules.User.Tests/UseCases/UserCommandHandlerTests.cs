using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Get;
using BJK.Modules.User.UseCases.Reset;
using BJK.Modules.User.UseCases.Update;
using FluentValidation;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class UserCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_creates_account_then_profile()
    {
        _identity.CreateAccountAsync("rider@example.com", "Passw0rd!", Arg.Any<CancellationToken>())
            .Returns(AccountResult.Success("00000000-0000-4000-8000-000000000001"));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        var id = await handler.Handle(new AddUserCommand(" rider@example.com ", "Passw0rd!", "Road Hog"), Ct);

        _users.Received(1).Add(Arg.Is<UserProfile>(u => u.Id == id && u.IdentityId == "00000000-0000-4000-8000-000000000001"));
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_turns_identity_errors_into_validation_errors()
    {
        _identity.CreateAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AccountResult.Failed([new AccountError("Password", "Too weak.")]));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new AddUserCommand("rider@example.com", "weak", "Road Hog"), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == "Password");
        _users.DidNotReceive().Add(Arg.Any<UserProfile>());
    }

    [Fact]
    public async Task Add_removes_the_account_if_the_profile_cannot_be_saved()
    {
        _identity.CreateAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AccountResult.Success("00000000-0000-4000-8000-000000000001"));
        _users.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new AddUserCommand("rider@example.com", "Passw0rd!", "Road Hog"), Ct));

        await _identity.Received(1).DeleteAccountAsync("00000000-0000-4000-8000-000000000001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_by_owner_changes_display_name()
    {
        var user = ExistingUser("00000000-0000-4000-8000-000000000001");
        CallerIs("00000000-0000-4000-8000-000000000001");
        var handler = new UpdateUserCommandHandler(_users, _caller, _clock);

        await handler.Handle(new UpdateUserCommand(user.Id, "Lane Hogger"), Ct);

        Assert.Equal("Lane Hogger", user.DisplayName);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_by_someone_else_is_forbidden()
    {
        var user = ExistingUser("00000000-0000-4000-8000-000000000001");
        CallerIs("00000000-0000-4000-8000-000000000002");
        var handler = new UpdateUserCommandHandler(_users, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new UpdateUserCommand(user.Id, "Hijacked"), Ct));

        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_by_admin_is_allowed()
    {
        var user = ExistingUser("00000000-0000-4000-8000-000000000001");
        _caller.HasScope(UserModule.ManageScope).Returns(true);
        var handler = new UpdateUserCommandHandler(_users, _caller, _clock);

        await handler.Handle(new UpdateUserCommand(user.Id, "Renamed by admin"), Ct);

        Assert.Equal("Renamed by admin", user.DisplayName);
    }

    [Fact]
    public async Task Get_of_missing_user_is_not_found()
    {
        CallerIs("00000000-0000-4000-8000-000000000001");
        var handler = new GetUserQueryHandler(_users, _caller);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetUserQuery(Guid.NewGuid()), Ct));
    }

    [Fact]
    public async Task GetCurrent_returns_callers_profile()
    {
        var user = ExistingUser("00000000-0000-4000-8000-000000000001");
        _users.GetByIdentityIdAsync("00000000-0000-4000-8000-000000000001", Arg.Any<CancellationToken>()).Returns(user);
        CallerIs("00000000-0000-4000-8000-000000000001");
        var handler = new GetCurrentUserQueryHandler(_users, _caller);

        var dto = await handler.Handle(new GetCurrentUserQuery(), Ct);

        Assert.Equal(user.Id, dto.Id);
    }

    [Fact]
    public async Task Delete_disables_account_and_soft_deletes_profile()
    {
        var user = ExistingUser("00000000-0000-4000-8000-000000000001");
        CallerIs("00000000-0000-4000-8000-000000000001");
        var handler = new DeleteUserCommandHandler(_users, _identity, _caller, _clock);

        await handler.Handle(new DeleteUserCommand(user.Id), Ct);

        Assert.True(user.IsDeleted);
        await _identity.Received(1).DisableAccountAsync("00000000-0000-4000-8000-000000000001", Arg.Any<CancellationToken>());
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetPassword_failure_is_a_validation_error()
    {
        _identity.ResetPasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AccountResult.Failed([new AccountError("Token", "Invalid token.")]));
        var handler = new ResetPasswordCommandHandler(_identity);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new ResetPasswordCommand("rider@example.com", "bad", "N3wPassw0rd!"), Ct));
    }

    private void CallerIs(string identityId) => _caller.IdentityId.Returns(identityId);

    private UserProfile ExistingUser(string identityId)
    {
        var user = UserProfile.Create(identityId, "rider@example.com", "Road Hog", Now);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }
}
