using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.User.Domain;
using BJK.Modules.User.Infrastructure;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.AgeChecks;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace BJK.Modules.User.Tests.UseCases;

public sealed class AgeCheckHandlerTests
{
    private const string User1 = "00000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly ILivenessChecker _liveness = Substitute.For<ILivenessChecker>();
    private readonly IAgeEstimator _estimator = Substitute.For<IAgeEstimator>();
    private readonly IDocumentAgeCheck _document = Substitute.For<IDocumentAgeCheck>();
    private readonly AgeAssuranceOptions _settings = new();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly UserAccount _account;

    public AgeCheckHandlerTests()
    {
        _liveness.Name.Returns("Development");
        _estimator.Name.Returns("Development");
        _document.Name.Returns("Development");
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>()).Returns(LivenessResult.Live);
        _account = UserAccount.Create(User1, TestDates.Adult, Now.AddDays(-1));
        _users.GetByIdAsync(_account.Id, Arg.Any<CancellationToken>()).Returns(_account);
        _caller.IdentityId.Returns(User1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Face_estimate_of_25_or_over_passes()
    {
        EstimateIs(31);

        var result = await Face().Handle(FaceCommand(), Ct);

        Assert.Equal(AgeCheckStatus.Passed, result.Status);
        Assert.True(_account.IsAgeVerified);
        Assert.False(result.CanUseFaceCheck);
        Assert.False(result.CanUseDocumentCheck);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Face_estimate_of_18_to_24_offers_photo_id()
    {
        EstimateIs(21);

        var result = await Face().Handle(FaceCommand(), Ct);

        Assert.Equal(AgeCheckStatus.DocumentRequired, result.Status);
        Assert.Equal(AgeCheckResult.Under25, result.Result);
        Assert.True(result.CanUseDocumentCheck);
        Assert.False(result.CanUseFaceCheck);
        Assert.Contains("photo of your ID", result.Message);
        Assert.DoesNotContain("21", result.Message);
    }

    [Fact]
    public async Task Face_estimate_clearly_under_18_fails_kindly()
    {
        EstimateIs(14);

        var result = await Face().Handle(FaceCommand(), Ct);

        Assert.Equal(AgeCheckStatus.Failed, result.Status);
        Assert.False(_account.IsAgeVerified);
        Assert.Contains("18 and over", result.Message);
    }

    [Fact]
    public async Task Failed_liveness_skips_the_estimate_and_gives_a_hint()
    {
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>())
            .Returns(LivenessResult.Failed(LivenessFailure.NoHeadTurn));

        var result = await Face().Handle(FaceCommand(), Ct);

        Assert.Equal(AgeCheckStatus.NotChecked, result.Status);
        Assert.Equal(AgeCheckResult.LivenessFailed, result.Result);
        Assert.Equal(2, result.AttemptsLeft);
        Assert.Contains("turn your head", result.Message);
        await _estimator.DidNotReceive().EstimateAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_video_is_wiped_after_the_check()
    {
        EstimateIs(30);
        var command = FaceCommand();

        await Face().Handle(command, Ct);

        Assert.All(command.Video, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task The_video_is_wiped_even_when_the_provider_fails_and_the_attempt_is_not_counted()
    {
        _estimator.EstimateAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("provider down"));
        var command = FaceCommand();

        await Assert.ThrowsAsync<HttpRequestException>(() => Face().Handle(command, Ct));

        Assert.All(command.Video, b => Assert.Equal(0, b));
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_three_attempts_a_day()
    {
        _liveness.CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>())
            .Returns(LivenessResult.Failed(LivenessFailure.NoFace));
        var handler = Face();
        for (var i = 0; i < 3; i++)
        {
            await handler.Handle(FaceCommand(), Ct);
        }

        var ex = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(FaceCommand(), Ct));

        Assert.Contains("tries for today", ex.Errors.Single().ErrorMessage);
        await _liveness.Received(3).CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Selfie_is_refused_once_photo_id_is_required()
    {
        _account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Under25, "Development", Now);

        await Assert.ThrowsAsync<ValidationException>(() => Face().Handle(FaceCommand(), Ct));
    }

    [Fact]
    public async Task Already_passed_accounts_are_not_checked_again()
    {
        _account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Passed, "Development", Now);

        await Assert.ThrowsAsync<ValidationException>(() => Face().Handle(FaceCommand(), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => Document().Handle(DocumentCommand(), Ct));
    }

    [Fact]
    public async Task Photo_id_of_18_or_over_passes_after_under_25()
    {
        _account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Under25, "Development", Now);
        _document.CheckAsync(Arg.Any<DocumentImage>(), Arg.Any<CancellationToken>()).Returns(new DocumentAgeResult(19, "Development"));
        var command = DocumentCommand();

        var result = await Document().Handle(command, Ct);

        Assert.Equal(AgeCheckStatus.Passed, result.Status);
        Assert.Equal(AgeCheckMethod.Document, result.Method);
        Assert.All(command.Image, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Photo_id_under_18_is_final()
    {
        _document.CheckAsync(Arg.Any<DocumentImage>(), Arg.Any<CancellationToken>()).Returns(new DocumentAgeResult(16, "Development"));

        var result = await Document().Handle(DocumentCommand(), Ct);

        Assert.Equal(AgeCheckStatus.Failed, result.Status);
        Assert.False(result.CanUseDocumentCheck);
        Assert.False(result.CanUseFaceCheck);
        await Assert.ThrowsAsync<ValidationException>(() => Document().Handle(DocumentCommand(), Ct));
    }

    [Fact]
    public async Task Status_query_shows_what_the_user_can_do()
    {
        var handler = new GetMyAgeCheckQueryHandler(_users, _caller, Options.Create(_settings), _clock);

        var result = await handler.Handle(new GetMyAgeCheckQuery(), Ct);

        Assert.Equal(AgeCheckStatus.NotChecked, result.Status);
        Assert.Equal(3, result.AttemptsLeft);
        Assert.True(result.CanUseFaceCheck);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Under_18_by_date_of_birth_cannot_start_the_check()
    {
        var minor = UserAccount.Create("00000000-0000-4000-8000-000000000015", TestDates.Fifteen, Now.AddDays(-1));
        _users.GetByIdAsync(minor.Id, Arg.Any<CancellationToken>()).Returns(minor);
        _caller.IdentityId.Returns("00000000-0000-4000-8000-000000000015");

        var status = await new GetMyAgeCheckQueryHandler(_users, _caller, Options.Create(_settings), _clock)
            .Handle(new GetMyAgeCheckQuery(), Ct);
        var face = await Assert.ThrowsAsync<ValidationException>(() => Face().Handle(FaceCommand(), Ct));
        var document = await Assert.ThrowsAsync<ValidationException>(() => Document().Handle(DocumentCommand(), Ct));

        Assert.True(status.UnderAgeByDateOfBirth);
        Assert.False(status.CanUseFaceCheck);
        Assert.False(status.CanUseDocumentCheck);
        Assert.Contains("only for adults", status.Message);
        Assert.Contains("only for adults", face.Errors.Single().ErrorMessage);
        Assert.Contains("only for adults", document.Errors.Single().ErrorMessage);
        await _liveness.DidNotReceive().CheckAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, minor.AgeCheckAttempts);
    }

    [Fact]
    public async Task Passing_says_sensitive_posts_are_now_open()
    {
        EstimateIs(40);

        var result = await Face().Handle(FaceCommand(), Ct);

        Assert.Contains("sensitive posts", result.Message);
        Assert.False(result.UnderAgeByDateOfBirth);
    }

    [Fact]
    public async Task Only_admins_can_reset()
    {
        _account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Passed, "Development", Now);
        var handler = new ResetAgeCheckCommandHandler(_users, _identity, _caller, _clock, NullLogger<ResetAgeCheckCommandHandler>.Instance);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ResetAgeCheckCommand(_account.Id), Ct));
        Assert.True(_account.IsAgeVerified);

        _caller.HasScope(UserModule.ManageScope).Returns(true);
        _caller.HasRole(StaffRoles.Admin).Returns(true);
        _caller.UsedMfa.Returns(true);
        var dto = await handler.Handle(new ResetAgeCheckCommand(_account.Id), Ct);

        Assert.Equal(AgeCheckStatus.NotChecked, dto.AgeCheck.Status);
        Assert.False(dto.AgeVerified);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Data_export_includes_the_age_check_outcome_only()
    {
        _account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Passed, "Development", Now);
        var exporter = new AccountPersonalDataExporter(_users, _identity);

        var section = await exporter.ExportAsync(_account.Id, Ct);
        var json = JsonSerializer.Serialize(section, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        Assert.Contains("\"AgeCheck\":{\"Status\":\"Passed\",\"Method\":\"FaceEstimate\"", json);
        Assert.Contains("\"DateOfBirth\":\"1990-06-15\"", json);
        Assert.Contains("\"Liveness\":{\"Status\":\"NotChecked\"", json);
    }

    [Fact]
    public void Commands_never_print_their_bytes()
    {
        Assert.DoesNotContain("Byte", FaceCommand().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Byte", DocumentCommand().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Validators_check_size_and_type()
    {
        var options = Options.Create(new AgeAssuranceOptions { MaxVideoBytes = 10 });
        var validator = new SubmitFaceAgeCheckCommandValidator(options);

        Assert.True(validator.Validate(new SubmitFaceAgeCheckCommand(new byte[10], "video/webm;codecs=vp9")).IsValid);
        Assert.False(validator.Validate(new SubmitFaceAgeCheckCommand(new byte[11], "video/webm")).IsValid);
        Assert.False(validator.Validate(new SubmitFaceAgeCheckCommand([], "video/webm")).IsValid);
        Assert.False(validator.Validate(new SubmitFaceAgeCheckCommand(new byte[5], "image/gif")).IsValid);

        var documents = new SubmitDocumentAgeCheckCommandValidator(Options.Create(new AgeAssuranceOptions()));
        Assert.True(documents.Validate(new SubmitDocumentAgeCheckCommand(new byte[5], "image/jpeg")).IsValid);
        Assert.False(documents.Validate(new SubmitDocumentAgeCheckCommand(new byte[5], "application/pdf")).IsValid);
    }

    private void EstimateIs(double age) =>
        _estimator.EstimateAsync(Arg.Any<SelfieVideo>(), Arg.Any<CancellationToken>()).Returns(new AgeEstimate(age, 0, "Development"));

    private AgeAssuranceProviders Providers() => new(
        [_liveness],
        [_estimator],
        [_document],
        new StaticOptionsMonitor<AgeAssuranceOptions>(_settings));

    private SubmitFaceAgeCheckCommandHandler Face() => new(
        _users, _caller, Providers(), Options.Create(_settings), _clock, NullLogger<SubmitFaceAgeCheckCommandHandler>.Instance);

    private SubmitDocumentAgeCheckCommandHandler Document() => new(
        _users, _caller, Providers(), Options.Create(_settings), _clock, NullLogger<SubmitDocumentAgeCheckCommandHandler>.Instance);

    private static SubmitFaceAgeCheckCommand FaceCommand() => new(Enumerable.Repeat((byte)7, 64).ToArray(), "video/webm");

    private static SubmitDocumentAgeCheckCommand DocumentCommand() => new(Enumerable.Repeat((byte)9, 64).ToArray(), "image/jpeg");
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
