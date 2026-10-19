using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.SignUp;
using FluentValidation;
using NSubstitute;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class SignUpHandlerTests
{
    private const string User1 = "00000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public SignUpHandlerTests()
    {
        _caller.IdentityId.Returns(User1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_account_has_finished_sign_up()
    {
        var account = UserAccount.Create(User1, TestDates.Fifteen, Now);
        _users.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);

        var status = await new GetMySignUpQueryHandler(_users, _caller).Handle(new GetMySignUpQuery(), Ct);

        Assert.True(status.HasDateOfBirth);
        Assert.True(status.IsComplete);
    }

    [Fact]
    public async Task Older_accounts_add_their_date_of_birth_once()
    {
        var older = OlderAccount();
        var handler = new SetMyDateOfBirthCommandHandler(_users, _caller, _clock);
        Assert.False(older.IsSignUpComplete);

        var status = await handler.Handle(new SetMyDateOfBirthCommand(TestDates.Fifteen), Ct);

        Assert.True(status.IsComplete);
        Assert.Equal(TestDates.Fifteen, older.DateOfBirth);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new SetMyDateOfBirthCommand(TestDates.Adult), Ct));
        Assert.Equal(TestDates.Fifteen, older.DateOfBirth);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Setting_a_date_of_birth_under_13_is_refused_by_the_validator()
    {
        var validator = new SetMyDateOfBirthCommandValidator(_clock);

        var result = validator.Validate(new SetMyDateOfBirthCommand(new DateOnly(2014, 1, 1)));

        Assert.Equal(AgeRules.TooYoungErrorCode, Assert.Single(result.Errors).ErrorCode);
        Assert.DoesNotContain("2014", new SetMyDateOfBirthCommand(new DateOnly(2014, 1, 1)).ToString(), StringComparison.Ordinal);
    }

    /// <summary>An account created before dates of birth were asked for.</summary>
    private UserAccount OlderAccount()
    {
        var older = UserAccount.Create(User1, TestDates.Adult, Now.AddYears(-1));
        typeof(UserAccount).GetProperty(nameof(UserAccount.DateOfBirth))!.SetValue(older, null);
        _users.GetByIdAsync(older.Id, Arg.Any<CancellationToken>()).Returns(older);
        return older;
    }
}
