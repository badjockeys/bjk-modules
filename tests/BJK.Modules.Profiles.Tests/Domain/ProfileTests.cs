using BJK.Modules.Profiles.Domain;
using Xunit;
using static BJK.Modules.Profiles.Tests.TestData;

namespace BJK.Modules.Profiles.Tests.Domain;

public sealed class ProfileTests
{
    [Fact]
    public void Profile_id_is_the_user_id_and_values_are_tidied()
    {
        var profile = Profile.Create(UserId, "@Road_Hog", "  Road Hog ", "  ", " Bury ", Now);

        Assert.Equal(UserId, profile.Id);
        Assert.Equal("road_hog", profile.Handle);
        Assert.Equal("Road Hog", profile.DisplayName);
        Assert.Null(profile.Bio);
        Assert.Equal("Bury", profile.Location);
        Assert.Equal(Now, profile.CreatedAt);
    }

    [Fact]
    public void Create_rejects_a_bad_handle()
    {
        Assert.Throws<ArgumentException>(() => Profile.Create(UserId, "admin", "Road Hog", null, null, Now));
    }

    [Fact]
    public void SetAvatar_returns_the_previous_avatar_only_when_it_changes()
    {
        var profile = Profile.Create(UserId, "roadhog", "Road Hog", null, null, Now);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.Null(profile.SetAvatar(first, Now));
        Assert.Null(profile.SetAvatar(first, Now));
        Assert.Equal(first, profile.SetAvatar(second, Now));
        Assert.Equal(second, profile.SetAvatar(null, Now));
        Assert.Null(profile.AvatarMediaId);
    }
}
