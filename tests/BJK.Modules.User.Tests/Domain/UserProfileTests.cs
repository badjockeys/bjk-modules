using BJK.Modules.User.Domain;
using Xunit;

namespace BJK.Modules.User.Tests.Domain;

public sealed class UserProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_values_and_stamps_created_time()
    {
        var user = UserProfile.Create("00000000-0000-4000-8000-000000000001", "  rider@example.com ", "  Road Hog ", Now);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("rider@example.com", user.Email);
        Assert.Equal("Road Hog", user.DisplayName);
        Assert.Equal(Now, user.CreatedAt);
        Assert.False(user.IsDeleted);
    }

    [Fact]
    public void UpdateProfile_changes_display_name_and_stamps_updated_time()
    {
        var user = UserProfile.Create("00000000-0000-4000-8000-000000000001", "rider@example.com", "Road Hog", Now);

        user.UpdateProfile("Lane Hogger", Now.AddHours(1));

        Assert.Equal("Lane Hogger", user.DisplayName);
        Assert.Equal(Now.AddHours(1), user.UpdatedAt);
    }

    [Fact]
    public void Delete_is_soft_and_idempotent()
    {
        var user = UserProfile.Create("00000000-0000-4000-8000-000000000001", "rider@example.com", "Road Hog", Now);

        user.Delete(Now.AddHours(1));
        user.Delete(Now.AddHours(2));

        Assert.True(user.IsDeleted);
        Assert.Equal(Now.AddHours(1), user.DeletedAt);
    }

    [Fact]
    public void UpdateProfile_after_delete_throws()
    {
        var user = UserProfile.Create("00000000-0000-4000-8000-000000000001", "rider@example.com", "Road Hog", Now);
        user.Delete(Now);

        Assert.Throws<InvalidOperationException>(() => user.UpdateProfile("New name", Now));
    }

    [Fact]
    public void Profile_id_is_the_account_id()
    {
        var user = UserProfile.Create("6F9619FF-8B86-D011-B42D-00C04FC964FF", "rider@example.com", "Road Hog", Now);

        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), user.Id);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", user.IdentityId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Account_id_must_be_a_guid(string identityId)
    {
        Assert.Throws<ArgumentException>(() => UserProfile.Create(identityId, "rider@example.com", "Road Hog", Now));
    }

    [Fact]
    public void IsOwnedBy_matches_identity_id_exactly()
    {
        var user = UserProfile.Create("00000000-0000-4000-8000-000000000001", "rider@example.com", "Road Hog", Now);

        Assert.True(user.IsOwnedBy("00000000-0000-4000-8000-000000000001"));
        Assert.False(user.IsOwnedBy("SUB1"));
    }
}
