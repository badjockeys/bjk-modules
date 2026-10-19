using BJK.Modules.User.Domain;
using Xunit;

namespace BJK.Modules.User.Tests.Domain;

public sealed class AgeCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(25.0, 0.0, AgeCheckResult.Passed)]
    [InlineData(40.0, 0.0, AgeCheckResult.Passed)]
    [InlineData(24.9, 0.0, AgeCheckResult.Under25)]
    [InlineData(18.0, 0.0, AgeCheckResult.Under25)]
    [InlineData(17.9, 0.0, AgeCheckResult.Underage)]
    [InlineData(12.0, 0.0, AgeCheckResult.Underage)]
    [InlineData(27.0, 3.0, AgeCheckResult.Under25)]   // lower bound 24: not enough to pass
    [InlineData(28.0, 3.0, AgeCheckResult.Passed)]
    [InlineData(16.0, 3.0, AgeCheckResult.Under25)]   // upper bound 19: not clearly under 18
    [InlineData(14.0, 3.0, AgeCheckResult.Underage)]
    public void Face_estimates_follow_challenge_25(double age, double uncertainty, AgeCheckResult expected) =>
        Assert.Equal(expected, AgeCheckRules.DecideFaceEstimate(age, uncertainty, challengeAge: 25));

    [Fact]
    public void No_estimate_is_uncertain()
    {
        Assert.Equal(AgeCheckResult.Uncertain, AgeCheckRules.DecideFaceEstimate(null, 0, 25));
        Assert.Equal(AgeCheckResult.Uncertain, AgeCheckRules.DecideFaceEstimate(double.NaN, 0, 25));
        Assert.Equal(AgeCheckResult.Uncertain, AgeCheckRules.DecideFaceEstimate(0, 0, 25));
    }

    [Fact]
    public void Challenge_age_is_configurable() =>
        Assert.Equal(AgeCheckResult.Passed, AgeCheckRules.DecideFaceEstimate(21, 0, challengeAge: 21));

    [Theory]
    [InlineData(18, AgeCheckResult.Passed)]
    [InlineData(45, AgeCheckResult.Passed)]
    [InlineData(17, AgeCheckResult.Underage)]
    [InlineData(null, AgeCheckResult.DocumentUnreadable)]
    public void Documents_need_18(int? age, AgeCheckResult expected) =>
        Assert.Equal(expected, AgeCheckRules.DecideDocument(age));

    [Fact]
    public void New_accounts_are_not_checked_and_can_use_either_route()
    {
        var account = NewAccount();

        Assert.Equal(AgeCheckStatus.NotChecked, account.AgeCheckStatus);
        Assert.False(account.IsAgeVerified);
        Assert.True(account.CanUseFaceCheck);
        Assert.True(account.CanUseDocumentCheck);
        Assert.Equal(3, account.AgeCheckAttemptsLeft(Now, 3));
    }

    [Fact]
    public void Passing_verifies_the_account_and_closes_both_routes()
    {
        var account = NewAccount();

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Passed, "Development", Now);

        Assert.True(account.IsAgeVerified);
        Assert.Equal(AgeCheckMethod.FaceEstimate, account.AgeCheckMethod);
        Assert.Equal("Development", account.AgeCheckProvider);
        Assert.Equal(Now, account.AgeCheckRecordedAt);
        Assert.False(account.CanUseFaceCheck);
        Assert.False(account.CanUseDocumentCheck);
        Assert.Throws<InvalidOperationException>(
            () => account.RecordAgeCheck(AgeCheckMethod.Document, AgeCheckResult.Underage, "Development", Now));
    }

    [Fact]
    public void Under_25_needs_photo_id_and_closes_the_selfie_route()
    {
        var account = NewAccount();

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Under25, "Development", Now);

        Assert.Equal(AgeCheckStatus.DocumentRequired, account.AgeCheckStatus);
        Assert.False(account.CanUseFaceCheck);
        Assert.True(account.CanUseDocumentCheck);
    }

    [Fact]
    public void Inconclusive_attempts_leave_the_status_alone()
    {
        var account = NewAccount();

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.LivenessFailed, "YuNet", Now);
        Assert.Equal(AgeCheckStatus.NotChecked, account.AgeCheckStatus);
        Assert.Null(account.AgeCheckMethod);
        Assert.Equal(AgeCheckResult.LivenessFailed, account.AgeCheckResult);
        Assert.True(account.CanUseFaceCheck);

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Under25, "Development", Now);
        account.RecordAgeCheck(AgeCheckMethod.Document, AgeCheckResult.DocumentUnreadable, "Development", Now);
        Assert.Equal(AgeCheckStatus.DocumentRequired, account.AgeCheckStatus);
        Assert.Equal(AgeCheckMethod.FaceEstimate, account.AgeCheckMethod);
    }

    [Fact]
    public void Under_18_by_face_can_still_use_photo_id_but_under_18_by_photo_id_is_final()
    {
        var account = NewAccount();

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Underage, "Development", Now);
        Assert.Equal(AgeCheckStatus.Failed, account.AgeCheckStatus);
        Assert.False(account.CanUseFaceCheck);
        Assert.True(account.CanUseDocumentCheck);

        account.RecordAgeCheck(AgeCheckMethod.Document, AgeCheckResult.Underage, "Development", Now);
        Assert.Equal(AgeCheckStatus.Failed, account.AgeCheckStatus);
        Assert.Equal(AgeCheckMethod.Document, account.AgeCheckMethod);
        Assert.False(account.CanUseDocumentCheck);
    }

    [Fact]
    public void Attempts_are_limited_per_24_hours()
    {
        var account = NewAccount();

        Assert.True(account.TryStartAgeCheckAttempt(Now, 3));
        Assert.True(account.TryStartAgeCheckAttempt(Now.AddHours(1), 3));
        Assert.True(account.TryStartAgeCheckAttempt(Now.AddHours(2), 3));
        Assert.False(account.TryStartAgeCheckAttempt(Now.AddHours(3), 3));
        Assert.Equal(0, account.AgeCheckAttemptsLeft(Now.AddHours(23), 3));

        Assert.Equal(3, account.AgeCheckAttemptsLeft(Now.AddDays(1), 3));
        Assert.True(account.TryStartAgeCheckAttempt(Now.AddDays(1), 3));
        Assert.Equal(2, account.AgeCheckAttemptsLeft(Now.AddDays(1), 3));
    }

    [Fact]
    public void Reset_clears_everything_including_attempts()
    {
        var account = NewAccount();
        account.TryStartAgeCheckAttempt(Now, 1);
        account.RecordAgeCheck(AgeCheckMethod.Document, AgeCheckResult.Underage, "Development", Now);

        account.ResetAgeCheck(Now.AddMinutes(5));

        Assert.Equal(AgeCheckStatus.NotChecked, account.AgeCheckStatus);
        Assert.Null(account.AgeCheckMethod);
        Assert.Null(account.AgeCheckResult);
        Assert.Null(account.AgeCheckProvider);
        Assert.Null(account.AgeCheckRecordedAt);
        Assert.Equal(1, account.AgeCheckAttemptsLeft(Now.AddMinutes(5), 1));
    }

    [Fact]
    public void Long_provider_names_are_cut_to_fit()
    {
        var account = NewAccount();

        account.RecordAgeCheck(AgeCheckMethod.FaceEstimate, AgeCheckResult.Uncertain, new string('x', 300), Now);

        Assert.Equal(UserAccount.ProviderMaxLength, account.AgeCheckProvider!.Length);
    }

    private static UserAccount NewAccount() => UserAccount.Create("00000000-0000-4000-8000-000000000001", TestDates.Adult, Now.AddDays(-1));
}
