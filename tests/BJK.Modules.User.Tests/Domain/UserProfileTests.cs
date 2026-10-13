using BJK.Modules.User.Domain;
using Xunit;

namespace BJK.Modules.User.Tests.Domain;

public sealed class UserProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_trims_values_and_stamps_created_time()
    {
        var user = UserProfile.Create("sub-1", "  rider@example.com ", "  Road Hog ", Now);

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("rider@example.com", user.Email);
        Assert.Equal("Road Hog", user.DisplayName);
        Assert.Equal(Now, user.CreatedAt);
        Assert.False(user.IsDeleted);
    }

    [Fact]
    public void UpdateProfile_changes_display_name_and_stamps_updated_time()
    {
        var user = UserProfile.Create("sub-1", "rider@example.com", "Road Hog", Now);

        user.UpdateProfile("Lane Hogger", Now.AddHours(1));

        Assert.Equal("Lane Hogger", user.DisplayName);
        Assert.Equal(Now.AddHours(1), user.UpdatedAt);
    }

    [Fact]
    public void Delete_is_soft_and_idempotent()
    {
        var user = UserProfile.Create("sub-1", "rider@example.com", "Road Hog", Now);

        user.Delete(Now.AddHours(1));
        user.Delete(Now.AddHours(2));

        Assert.True(user.IsDeleted);
        Assert.Equal(Now.AddHours(1), user.DeletedAt);
    }

    [Fact]
    public void UpdateProfile_after_delete_throws()
    {
        var user = UserProfile.Create("sub-1", "rider@example.com", "Road Hog", Now);
        user.Delete(Now);

        Assert.Throws<InvalidOperationException>(() => user.UpdateProfile("New name", Now));
    }

    [Fact]
    public void IsOwnedBy_matches_identity_id_exactly()
    {
        var user = UserProfile.Create("sub-1", "rider@example.com", "Road Hog", Now);

        Assert.True(user.IsOwnedBy("sub-1"));
        Assert.False(user.IsOwnedBy("SUB-1"));
    }
}
