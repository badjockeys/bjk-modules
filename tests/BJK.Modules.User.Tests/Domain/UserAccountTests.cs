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
        var account = UserAccount.Create("6F9619FF-8B86-D011-B42D-00C04FC964FF", Now);

        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), account.Id);
        Assert.True(account.IsActive);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Account_id_must_be_a_guid(string identityId)
    {
        Assert.Throws<ArgumentException>(() => UserAccount.Create(identityId, Now));
    }

    [Fact]
    public void Deactivate_starts_a_90_day_grace_period()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), Now);

        Assert.True(account.Deactivate(Now, Grace));

        Assert.Equal(UserAccountStatus.Deactivated, account.Status);
        Assert.Equal(Now.AddDays(90), account.DeletionDueAt);
        Assert.False(account.IsDueForDeletion(Now.AddDays(89)));
        Assert.True(account.IsDueForDeletion(Now.AddDays(90)));
    }

    [Fact]
    public void Deactivating_twice_keeps_the_first_deadline()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), Now);
        account.Deactivate(Now, Grace);

        Assert.False(account.Deactivate(Now.AddDays(10), Grace));
        Assert.Equal(Now.AddDays(90), account.DeletionDueAt);
    }

    [Fact]
    public void Reactivate_cancels_the_deletion()
    {
        var account = UserAccount.Create(Guid.NewGuid().ToString(), Now);
        account.Deactivate(Now, Grace);

        Assert.True(account.Reactivate(Now.AddDays(30)));

        Assert.True(account.IsActive);
        Assert.Null(account.DeletionDueAt);
        Assert.False(account.IsDueForDeletion(Now.AddDays(365)));
        Assert.False(account.Reactivate(Now.AddDays(31)));
    }
}
