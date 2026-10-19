using BJK.Modules.Shared.Security;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.SignUp;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class SignUpHandlerTests
{
    private const string User1 = "00000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly ILivenessChecker _liveness = Substitute.For<ILivenessChecker>();
    private readonly IAgeEstimator _estimator = Substitute.For<IAgeEstimator>();
    private readonly IDocumentAgeCheck _document = Substitute.For<IDocumentAgeCheck>();
    private readonly AgeAssuranceOptions _settings = new();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly UserAccount _account;

    public SignUpHandlerTests()
    {
        _liveness.Name.Returns("Development");
        _estimator.Name.Returns("Development");
        _document.Name.Returns("Development");
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>()).Returns(LivenessResult.Live);
        _account = UserAccount.Create(User1, TestDates.Fifteen, Now.AddMinutes(-5));
        _users.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _caller.IdentityId.Returns(User1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_account_must_pass_liveness_to_finish_sign_up()
    {
        var status = await new GetMySignUpQueryHandler(_users, _caller, Options.Create(_settings), _clock).Handle(new GetMySignUpQuery(), Ct);

        Assert.True(status.HasDateOfBirth);
        Assert.Equal(LivenessStatus.NotChecked, status.LivenessStatus);
        Assert.False(status.IsComplete);
        Assert.True(status.CanTryLiveness);
        Assert.Equal(3, status.AttemptsLeft);
    }

    [Fact]
    public async Task Passing_liveness_finishes_sign_up_without_any_age_estimate()
    {
        var command = Video();

        var status = await Liveness().Handle(command, Ct);

        Assert.True(status.IsComplete);
        Assert.Equal(LivenessStatus.Passed, status.LivenessStatus);
        Assert.Equal(Now, _account.LivenessCheckedAt);
        Assert.False(_account.IsAgeVerified);
        Assert.Equal(AgeCheckStatus.NotChecked, _account.AgeCheckStatus);
        Assert.Contains("Welcome", status.Message);
        Assert.All(command.Video, b => Assert.Equal(0, b));
        await _estimator.DidNotReceive().EstimateAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>());
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failing_liveness_says_why_and_how_many_tries_are_left()
    {
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>())
            .Returns(LivenessResult.Failed(LivenessFailure.NoHeadTurn));

        var status = await Liveness().Handle(Video(), Ct);

        Assert.False(status.IsComplete);
        Assert.Equal(LivenessStatus.Failed, status.LivenessStatus);
        Assert.Equal(2, status.AttemptsLeft);
        Assert.True(status.CanTryLiveness);
        Assert.Contains("turn your head", status.Message);
        Assert.Contains("2 tries left today", status.Message);
    }

    [Fact]
    public async Task Liveness_allows_three_tries_a_day()
    {
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>())
            .Returns(LivenessResult.Failed(LivenessFailure.NoFace));
        var handler = Liveness();
        SignUpStatusDto? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await handler.Handle(Video(), Ct);
        }

        var ex = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(Video(), Ct));

        Assert.False(last!.CanTryLiveness);
        Assert.Contains("come back tomorrow", last.Message);
        Assert.Contains("tries for today", ex.Errors.Single().ErrorMessage);
        await _liveness.Received(3).CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_video_is_wiped_even_when_the_checker_fails_and_nothing_is_saved()
    {
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("model missing"));
        var command = Video();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Liveness().Handle(command, Ct));

        Assert.All(command.Video, b => Assert.Equal(0, b));
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Passed_liveness_is_not_checked_again()
    {
        await Liveness().Handle(Video(), Ct);

        await Assert.ThrowsAsync<ValidationException>(() => Liveness().Handle(Video(), Ct));
    }

    [Fact]
    public async Task Older_accounts_add_their_date_of_birth_once()
    {
        var older = UserAccount.Create(User1, TestDates.Adult, Now.AddYears(-1));
        typeof(UserAccount).GetProperty(nameof(UserAccount.DateOfBirth))!.SetValue(older, null);
        _users.GetByIdAsync(older.Id, Arg.Any<CancellationToken>()).Returns(older);
        var handler = new SetMyDateOfBirthCommandHandler(_users, _caller, Options.Create(_settings), _clock);

        var status = await handler.Handle(new SetMyDateOfBirthCommand(TestDates.Fifteen), Ct);

        Assert.True(status.HasDateOfBirth);
        Assert.Equal(TestDates.Fifteen, older.DateOfBirth);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new SetMyDateOfBirthCommand(TestDates.Adult), Ct));
        Assert.Equal(TestDates.Fifteen, older.DateOfBirth);
    }

    [Fact]
    public void Setting_a_date_of_birth_under_13_is_refused_by_the_validator()
    {
        var validator = new SetMyDateOfBirthCommandValidator(_clock);

        var result = validator.Validate(new SetMyDateOfBirthCommand(new DateOnly(2014, 1, 1)));

        Assert.Equal(AgeRules.TooYoungErrorCode, Assert.Single(result.Errors).ErrorCode);
        Assert.DoesNotContain("2014", new SetMyDateOfBirthCommand(new DateOnly(2014, 1, 1)).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Liveness_validator_checks_size_and_type()
    {
        var validator = new SubmitSignUpLivenessCommandValidator(Options.Create(new AgeAssuranceOptions { MaxVideoBytes = 10 }));

        Assert.True(validator.Validate(new SubmitSignUpLivenessCommand(new byte[10], "video/mp4")).IsValid);
        Assert.False(validator.Validate(new SubmitSignUpLivenessCommand(new byte[11], "video/mp4")).IsValid);
        Assert.False(validator.Validate(new SubmitSignUpLivenessCommand([], "video/webm")).IsValid);
        Assert.False(validator.Validate(new SubmitSignUpLivenessCommand(new byte[5], "image/gif")).IsValid);
        Assert.DoesNotContain("Byte", Video().ToString(), StringComparison.Ordinal);
    }

    private SubmitSignUpLivenessCommandHandler Liveness() => new(
        _users,
        _caller,
        new AgeAssuranceProviders([_liveness], [_estimator], [_document], new StaticOptionsMonitor<AgeAssuranceOptions>(_settings)),
        Options.Create(_settings),
        _clock,
        NullLogger<SubmitSignUpLivenessCommandHandler>.Instance);

    private static SubmitSignUpLivenessCommand Video() => new(Enumerable.Repeat((byte)7, 64).ToArray(), "video/webm");
}
