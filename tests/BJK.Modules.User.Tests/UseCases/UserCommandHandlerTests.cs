using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Deactivate;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Get;
using BJK.Modules.User.UseCases.Reactivate;
using BJK.Modules.User.UseCases.Reset;
using BJK.Modules.User.UseCases.Roles;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class UserCommandHandlerTests
{
    private const string User1 = "00000000-0000-4000-8000-000000000001";
    private const string User2 = "00000000-0000-4000-8000-000000000002";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly AccountLifecycle _lifecycle;

    public UserCommandHandlerTests()
    {
        _lifecycle = new AccountLifecycle(_users, _identity, Options.Create(new UserOptions()), _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_creates_sign_in_account_then_account_record()
    {
        _identity.CreateAccountAsync("rider@example.com", "Passw0rd!", Arg.Any<CancellationToken>())
            .Returns(AccountResult.Success(User1));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        var id = await handler.Handle(new AddUserCommand(" rider@example.com ", "Passw0rd!"), Ct);

        Assert.Equal(Guid.Parse(User1), id);
        _users.Received(1).Add(Arg.Is<UserAccount>(u => u.Id == id && u.IsActive));
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _identity.Received(1).SendEmailConfirmationAsync(User1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_turns_identity_errors_into_validation_errors()
    {
        _identity.CreateAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AccountResult.Failed([new AccountError("Email", "Email is already taken.")]));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new AddUserCommand("rider@example.com", "Passw0rd!"), Ct));

        Assert.Contains(ex.Errors, e => e.PropertyName == "Email");
        _users.DidNotReceive().Add(Arg.Any<UserAccount>());
    }

    [Fact]
    public async Task Add_removes_the_sign_in_account_if_the_record_cannot_be_saved()
    {
        _identity.CreateAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AccountResult.Success(User1));
        _users.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        var handler = new AddUserCommandHandler(_users, _identity, _clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new AddUserCommand("rider@example.com", "Passw0rd!"), Ct));

        await _identity.Received(1).DeleteAccountAsync(User1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrent_combines_account_and_sign_in_details()
    {
        var account = Existing(User1);
        CallerIs(User1);
        _identity.GetAccountAsync(User1, Arg.Any<CancellationToken>()).Returns(new SignInAccount(User1, "rider@example.com", true, false, []));
        var handler = new GetCurrentUserQueryHandler(_users, _identity, _caller);

        var dto = await handler.Handle(new GetCurrentUserQuery(), Ct);

        Assert.Equal(account.Id, dto.Id);
        Assert.Equal("rider@example.com", dto.Email);
        Assert.Equal(UserAccountStatus.Active, dto.Status);
    }

    [Fact]
    public async Task Get_someone_elses_account_is_forbidden()
    {
        Existing(User2);
        CallerIs(User1);
        var handler = new GetUserQueryHandler(_users, _identity, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new GetUserQuery(Guid.Parse(User2)), Ct));
    }

    [Fact]
    public async Task Deactivate_needs_the_right_password()
    {
        var account = Existing(User1);
        CallerIs(User1);
        var handler = new DeactivateMyAccountCommandHandler(_users, _identity, _lifecycle, _caller);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new DeactivateMyAccountCommand("wrong"), Ct));

        Assert.True(account.IsActive);
        await _identity.DidNotReceive().DisableAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivate_stops_sign_in_and_publishes_an_event()
    {
        var account = Existing(User1);
        CallerIs(User1);
        _identity.CheckPasswordAsync(User1, "Passw0rd!", Arg.Any<CancellationToken>()).Returns(true);
        var handler = new DeactivateMyAccountCommandHandler(_users, _identity, _lifecycle, _caller);

        await handler.Handle(new DeactivateMyAccountCommand("Passw0rd!"), Ct);

        Assert.Equal(UserAccountStatus.Deactivated, account.Status);
        Assert.Equal(Now.AddDays(90), account.DeletionDueAt);
        await _identity.Received(1).DisableAccountAsync(User1, Arg.Any<CancellationToken>());
        _users.Received(1).AddEvent(Arg.Is<UserLifecycleEvent>(e => e.UserId == account.Id && e.Type == UserLifecycleEventType.Deactivated));
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reactivate_with_the_right_credentials_brings_the_account_back()
    {
        var account = Existing(User1);
        account.Deactivate(Now.AddDays(-10), TimeSpan.FromDays(90));
        _identity.FindByCredentialsAsync("rider@example.com", "Passw0rd!", Arg.Any<CancellationToken>()).Returns(User1);
        var handler = new ReactivateAccountCommandHandler(_users, _identity, _lifecycle);

        await handler.Handle(new ReactivateAccountCommand("rider@example.com", "Passw0rd!"), Ct);

        Assert.True(account.IsActive);
        await _identity.Received(1).EnableAccountAsync(User1, Arg.Any<CancellationToken>());
        _users.Received(1).AddEvent(Arg.Is<UserLifecycleEvent>(e => e.Type == UserLifecycleEventType.Reactivated));
    }

    [Fact]
    public async Task Reactivate_gives_the_same_answer_for_wrong_password_and_active_accounts()
    {
        Existing(User1);
        _identity.FindByCredentialsAsync("rider@example.com", "Passw0rd!", Arg.Any<CancellationToken>()).Returns(User1);
        var handler = new ReactivateAccountCommandHandler(_users, _identity, _lifecycle);

        var active = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new ReactivateAccountCommand("rider@example.com", "Passw0rd!"), Ct));
        var wrong = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new ReactivateAccountCommand("rider@example.com", "nope"), Ct));

        Assert.Equal(active.Errors.Single().ErrorMessage, wrong.Errors.Single().ErrorMessage);
        await _identity.DidNotReceive().EnableAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ordinary_users_cant_deactivate_other_accounts()
    {
        Existing(User2);
        CallerIs(User1);
        var handler = new DeactivateUserCommandHandler(_users, _identity, _lifecycle, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeactivateUserCommand(Guid.Parse(User2)), Ct));
    }

    [Fact]
    public async Task Moderators_deactivate_ordinary_users()
    {
        var target = Existing(User2);
        CallerIsStaff(User1, "Moderator");
        SignInIs(User2);
        var handler = new DeactivateUserCommandHandler(_users, _identity, _lifecycle, _caller);

        await handler.Handle(new DeactivateUserCommand(target.Id), Ct);

        Assert.Equal(UserAccountStatus.Deactivated, target.Status);
    }

    [Fact]
    public async Task Moderators_cant_deactivate_other_staff()
    {
        var target = Existing(User2);
        CallerIsStaff(User1, "Moderator");
        SignInIs(User2, "Moderator");
        var handler = new DeactivateUserCommandHandler(_users, _identity, _lifecycle, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeactivateUserCommand(target.Id), Ct));
        Assert.True(target.IsActive);
    }

    [Fact]
    public async Task Admins_can_deactivate_staff()
    {
        var target = Existing(User2);
        CallerIsStaff(User1, "Admin");
        SignInIs(User2, "Moderator");
        var handler = new DeactivateUserCommandHandler(_users, _identity, _lifecycle, _caller);

        await handler.Handle(new DeactivateUserCommand(target.Id), Ct);

        Assert.False(target.IsActive);
    }

    [Fact]
    public async Task Only_admins_set_roles()
    {
        var target = Existing(User2);
        CallerIsStaff(User1, "Moderator");
        var handler = new SetRolesCommandHandler(_users, _identity, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new SetRolesCommand(target.Id, ["Moderator"]), Ct));
        await _identity.DidNotReceive().SetRolesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admins_make_someone_a_moderator()
    {
        var target = Existing(User2);
        CallerIsStaff(User1, "Admin");
        var handler = new SetRolesCommandHandler(_users, _identity, _caller);

        await handler.Handle(new SetRolesCommand(target.Id, ["Moderator"]), Ct);

        await _identity.Received(1).SetRolesAsync(User2, Arg.Is<IReadOnlyCollection<string>>(r => r.Single() == "Moderator"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admins_cant_remove_their_own_admin_role()
    {
        var me = Existing(User1);
        CallerIsStaff(User1, "Admin");
        var handler = new SetRolesCommandHandler(_users, _identity, _caller);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new SetRolesCommand(me.Id, []), Ct));
    }

    [Fact]
    public async Task Retention_deletes_expired_accounts_and_publishes_deleted_events()
    {
        var expired = Existing(User1);
        expired.Deactivate(Now.AddDays(-91), TimeSpan.FromDays(90));
        _users.ListDueForDeletionAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<UserAccount> { expired });
        var handler = new DeleteExpiredAccountsCommandHandler(_users, _lifecycle, _clock, NullLogger<DeleteExpiredAccountsCommandHandler>.Instance);

        var deleted = await handler.Handle(new DeleteExpiredAccountsCommand(), Ct);

        Assert.Equal(1, deleted);
        await _identity.Received(1).DeleteAccountAsync(User1, Arg.Any<CancellationToken>());
        _users.Received(1).Remove(expired);
        _users.Received(1).AddEvent(Arg.Is<UserLifecycleEvent>(e => e.UserId == expired.Id && e.Type == UserLifecycleEventType.Deleted));
    }

    [Fact]
    public async Task Retention_carries_on_past_an_account_that_fails()
    {
        var first = Existing(User1);
        first.Deactivate(Now.AddDays(-91), TimeSpan.FromDays(90));
        var second = Existing(User2);
        second.Deactivate(Now.AddDays(-91), TimeSpan.FromDays(90));
        _users.ListDueForDeletionAsync(Now, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<UserAccount> { first, second });
        _identity.DeleteAccountAsync(User1, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var handler = new DeleteExpiredAccountsCommandHandler(_users, _lifecycle, _clock, NullLogger<DeleteExpiredAccountsCommandHandler>.Instance);

        var deleted = await handler.Handle(new DeleteExpiredAccountsCommand(), Ct);

        Assert.Equal(1, deleted);
        _users.Received(1).DiscardChanges();
        _users.Received(1).Remove(second);
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

    private void CallerIsStaff(string identityId, string role)
    {
        CallerIs(identityId);
        _caller.HasScope(UserModule.ManageScope).Returns(true);
        _caller.HasRole(role).Returns(true);
        _caller.UsedMfa.Returns(true);
    }

    private void SignInIs(string identityId, params string[] roles) =>
        _identity.GetAccountAsync(identityId, Arg.Any<CancellationToken>())
            .Returns(new SignInAccount(identityId, identityId + "@example.com", true, false, roles));

    private UserAccount Existing(string identityId)
    {
        var account = UserAccount.Create(identityId, Now.AddYears(-1));
        _users.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        return account;
    }
}
