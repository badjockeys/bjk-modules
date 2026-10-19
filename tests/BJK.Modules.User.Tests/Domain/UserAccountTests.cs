using BJK.Modules.User.Domain;
using Xunit;

namespace BJK.Modules.User.Tests.Domain;

public sealed class UserAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(90);

    [Fact]
    public void Account_id_is_the_sign_in_account_id()
    {
        var account = UserAccount.Create("6F9619FF-8B86-D011-B42D-00C04FC964FF", TestDates.Adult, Now);

        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), account.Id);
        Assert.True(account.IsActive);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Account_id_must_be_a_guid(string identityId)
    {
        Assert.Throws<ArgumentException>(() => UserAccount.Create(identityId, TestDates.Adult, Now));
    }

    [Fact]
    public void Deactivate_starts_a_90_day_grace_period()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);

        Assert.True(account.Deactivate(Now, Grace));

        Assert.Equal(UserAccountStatus.Deactivated, account.Status);
        Assert.Equal(Now.AddDays(90), account.DeletionDueAt);
        Assert.False(account.IsDueForDeletion(Now.AddDays(89)));
        Assert.True(account.IsDueForDeletion(Now.AddDays(90)));
    }

    [Fact]
    public void Deactivating_twice_keeps_the_first_deadline()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);
        account.Deactivate(Now, Grace);

        Assert.False(account.Deactivate(Now.AddDays(10), Grace));
        Assert.Equal(Now.AddDays(90), account.DeletionDueAt);
    }

    [Fact]
    public void Reactivate_cancels_the_deletion()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);
        account.Deactivate(Now, Grace);

        Assert.True(account.Reactivate(Now.AddDays(30)));

        Assert.True(account.IsActive);
        Assert.Null(account.DeletionDueAt);
        Assert.False(account.IsDueForDeletion(Now.AddDays(365)));
        Assert.False(account.Reactivate(Now.AddDays(31)));
    }

    [Fact]
    public void New_accounts_keep_the_date_of_birth()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Fifteen, Now);

        Assert.Equal(TestDates.Fifteen, account.DateOfBirth);
        Assert.True(account.IsMinorOn(DateOnly.FromDateTime(Now.UtcDateTime)));
        Assert.False(account.IsSignUpComplete);
    }

    [Theory]
    [InlineData(2013, 10, 19)] // 12, turning 13 tomorrow
    [InlineData(2027, 1, 1)] // in the future
    [InlineData(1900, 1, 1)] // over 120
    public void Accounts_need_a_believable_date_of_birth_of_13_or_over(int year, int month, int day)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserAccount.Create(Guid.NewGuid().ToString(), new DateOnly(year, month, day), Now));
    }

    [Fact]
    public void Thirteenth_birthday_is_old_enough()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), new DateOnly(2013, 10, 18), Now);

        Assert.True(account.IsMinorOn(DateOnly.FromDateTime(Now.UtcDateTime)));
    }

    [Fact]
    public void Minor_status_lapses_on_the_18th_birthday()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), new DateOnly(2008, 10, 19), Now);

        Assert.True(account.IsMinorOn(new DateOnly(2026, 10, 18)));
        Assert.False(account.IsMinorOn(new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public void Liveness_pass_completes_sign_up()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);

        Assert.True(account.TryStartLivenessAttempt(Now, 3));
        account.RecordLiveness(passed: true, Now);

        Assert.True(account.IsSignUpComplete);
        Assert.Equal(LivenessStatus.Passed, account.LivenessStatus);
        Assert.Equal(Now, account.LivenessCheckedAt);
        Assert.False(account.TryStartLivenessAttempt(Now, 3));
        Assert.Throws<InvalidOperationException>(() => account.RecordLiveness(passed: false, Now));
    }

    [Fact]
    public void Liveness_allows_three_tries_a_day()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(account.TryStartLivenessAttempt(Now.AddMinutes(i), 3));
            account.RecordLiveness(passed: false, Now.AddMinutes(i));
        }

        Assert.False(account.TryStartLivenessAttempt(Now.AddHours(1), 3));
        Assert.Equal(0, account.LivenessAttemptsLeft(Now.AddHours(1), 3));
        Assert.Equal(3, account.LivenessAttemptsLeft(Now.AddDays(1), 3));
        Assert.True(account.TryStartLivenessAttempt(Now.AddDays(1), 3));
        Assert.False(account.IsSignUpComplete);
    }

    [Fact]
    public void Date_of_birth_can_be_added_once_to_an_older_account()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), TestDates.Adult, Now);

        Assert.Throws<InvalidOperationException>(() => account.SetDateOfBirth(TestDates.Fifteen, Now));
    }
}
