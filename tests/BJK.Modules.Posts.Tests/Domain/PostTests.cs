using BJK.Modules.Posts.Domain;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Domain;

public sealed class PostTests
{
    [Fact]
    public void Create_publishes_with_media_in_order()
    {
        var media = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var post = Post.Create(UserId, Details(), media, Now);

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(media, post.Media.OrderBy(m => m.Position).Select(m => m.MediaId));
        Assert.All(post.Media, m => Assert.Equal(post.Id, m.PostId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Create_needs_one_to_ten_media(int count)
    {
        var media = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

        Assert.Throws<ArgumentException>(() => Post.Create(UserId, Details(), media, Now));
    }

    [Fact]
    public void Create_rejects_the_same_media_twice()
    {
        var id = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => Post.Create(UserId, Details(), [id, id], Now));
    }

    [Fact]
    public void Location_is_rounded_to_about_500_metres()
    {
        var details = Details() with { Latitude = 53.12345678m, Longitude = -2.98765432m };

        var post = Post.Create(UserId, details, [Guid.NewGuid()], Now);

        Assert.Equal(53.1225m, post.Latitude);
        Assert.InRange(post.Longitude, -2.984473m, -2.984471m);
    }

    [Theory]
    [InlineData(53.5933, -2.2966)]
    [InlineData(51.5007, -0.1246)]
    [InlineData(-33.8568, 151.2153)]
    [InlineData(0.0001, 0.0001)]
    [InlineData(89.9999, 179.9999)]
    public void Rounded_pins_are_within_about_400_metres_and_stable(double latitude, double longitude)
    {
        var (lat, lng) = PinRounding.Round((decimal)latitude, (decimal)longitude);

        // Half a 500 m grid cell in each direction is at most about 354 m away.
        var northSouthMetres = Math.Abs((double)lat - latitude) * 111_320;
        var eastWestMetres = Math.Abs((double)lng - longitude) * 111_320 * Math.Cos(latitude * Math.PI / 180);
        Assert.True(Math.Sqrt((northSouthMetres * northSouthMetres) + (eastWestMetres * eastWestMetres)) < 400 || latitude > 89);
        Assert.Equal((lat, lng), PinRounding.Round(lat, lng));
        Assert.InRange(lat, -90m, 90m);
        Assert.InRange(lng, -180m, 180m);
    }

    [Fact]
    public void Nearby_pins_round_to_the_same_point()
    {
        var a = PinRounding.Round(53.59330m, -2.29660m);
        var b = PinRounding.Round(53.59340m, -2.29650m);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Hide_and_unhide()
    {
        var post = NewPost(UserId);

        post.Hide("Shows a face", Now);
        Assert.Equal(PostStatus.Hidden, post.Status);
        Assert.Equal("Shows a face", post.HiddenReason);

        post.Unhide(Now);
        Assert.True(post.IsPublished);
        Assert.Null(post.HiddenReason);
    }

    [Fact]
    public void A_deleted_post_cant_be_edited()
    {
        var post = NewPost(UserId);
        post.Delete(Now);

        Assert.Throws<InvalidOperationException>(() => post.Update(Details("New title"), Now));
    }

    [Fact]
    public void Report_resolution_records_the_decision()
    {
        var report = Report.Create(OtherUserId, ReportTargetType.Post, Guid.NewGuid(), Guid.NewGuid(), ReportReason.Spam, "  advert ", Now);

        report.Resolve(ReportAction.HideContent, UserId, "Spam", Now);

        Assert.Equal("advert", report.Details);
        Assert.Equal(ReportStatus.ContentHidden, report.Status);
        Assert.Throws<InvalidOperationException>(() => report.Resolve(ReportAction.Dismiss, UserId, null, Now));
    }

    [Fact]
    public void Automatic_tags_wait_for_review_and_repeats_or_undone_tags_change_nothing()
    {
        var post = NewPost(UserId);

        Assert.True(post.MarkSensitiveAutomatically(0.4, Now));
        Assert.Equal((true, PostSensitiveSource.Automatic, SensitiveReviewStatus.Pending), (post.IsSensitive, post.SensitiveSource, post.SensitiveReview));
        Assert.False(post.MarkSensitiveAutomatically(0.6, Now));
        Assert.Equal(0.6, post.SensitiveScore);
        Assert.False(post.AuthorMayRemoveSensitive);

        post.ReviewAutomaticSensitive(confirm: false, OtherUserId, Now);
        Assert.Equal((false, PostSensitiveSource.None, SensitiveReviewStatus.Undone), (post.IsSensitive, post.SensitiveSource, post.SensitiveReview));
        Assert.Equal(OtherUserId, post.SensitiveReviewedBy);
        Assert.False(post.MarkSensitiveAutomatically(0.9, Now));
        Assert.False(post.IsSensitive);
        Assert.Throws<InvalidOperationException>(() => post.ReviewAutomaticSensitive(true, OtherUserId, Now));
    }

    [Fact]
    public void Authors_tag_their_own_posts_and_can_remove_only_their_own_tag()
    {
        var post = Post.Create(UserId, Details(), [Guid.NewGuid()], Now, isSensitive: true);

        Assert.Equal(PostSensitiveSource.Author, post.SensitiveSource);
        Assert.True(post.AuthorMayRemoveSensitive);

        post.SetSensitive(true, Now, PostSensitiveSource.Moderator);
        Assert.Equal(PostSensitiveSource.Author, post.SensitiveSource);

        post.SetSensitive(false, Now, PostSensitiveSource.Moderator);
        post.SetSensitive(true, Now, PostSensitiveSource.Moderator);
        Assert.Equal(PostSensitiveSource.Moderator, post.SensitiveSource);
        Assert.False(post.AuthorMayRemoveSensitive);
    }

    [Fact]
    public void A_post_keeps_every_type_the_main_one_first()
    {
        var details = Details() with { Types = [IncidentCategory.Tailgating, IncidentCategory.ShoutingOrGestures, IncidentCategory.Tailgating] };

        var post = Post.Create(UserId, details, [Guid.NewGuid()], Now);

        Assert.Equal(IncidentCategory.Tailgating, post.Category);
        Assert.Equal(new[] { IncidentCategory.Tailgating, IncidentCategory.ShoutingOrGestures }, post.Types);
        Assert.Equal(IncidentTypeFlags.Bit(IncidentCategory.Tailgating) | IncidentTypeFlags.Bit(IncidentCategory.ShoutingOrGestures), new TypeMask(post.TypeFlags, post.TypeFlagsHigh));
        Assert.Equal(IncidentGroup.FollowingTooClose, post.Group);
    }

    [Fact]
    public void Without_types_a_post_has_just_its_category()
    {
        var post = Post.Create(UserId, Details() with { Category = IncidentCategory.DoubleYellows }, [Guid.NewGuid()], Now);

        Assert.Equal(new[] { IncidentCategory.DoubleYellows }, post.Types);
        Assert.Equal(IncidentTypeFlags.Bit(IncidentCategory.DoubleYellows), new TypeMask(post.TypeFlags, post.TypeFlagsHigh));
    }

    [Fact]
    public void Editing_replaces_the_types()
    {
        var post = Post.Create(UserId, Details() with { Types = [IncidentCategory.SpeedingInTown, IncidentCategory.ClosePassCyclist] }, [Guid.NewGuid()], Now);

        post.Update(Details() with { Types = [IncidentCategory.PhoneInHand] }, Now);

        Assert.Equal(IncidentCategory.PhoneInHand, post.Category);
        Assert.Equal(new[] { IncidentCategory.PhoneInHand }, post.Types);
    }

    [Fact]
    public void Rows_from_before_types_read_as_their_category()
    {
        Assert.Equal(new[] { IncidentCategory.VehiclesOther }, IncidentTypeFlags.ToList(default, IncidentCategory.VehiclesOther));
        Assert.False(IncidentTypeFlags.IsOnTheRoad(IncidentCategory.DoubleYellows));
        Assert.True(IncidentTypeFlags.IsOnTheRoad(IncidentCategory.Tailgating));
    }

    [Fact]
    public void Vehicle_details_come_from_the_lists_and_need_a_type()
    {
        var post = Post.Create(UserId, Details() with { Vehicle = new VehicleDetails(VehicleType.Van, "ford", "TRANSIT custom", VehicleColour.White), ReportedToPolice = true }, [Guid.NewGuid()], Now);

        Assert.Equal(VehicleType.Van, post.VehicleType);
        Assert.Equal("Ford", post.VehicleMake);
        Assert.Equal("Transit Custom", post.VehicleModel);
        Assert.Equal(VehicleColour.White, post.VehicleColour);
        Assert.True(post.ReportedToPolice);

        Assert.Throws<ArgumentException>(() => Post.Create(UserId, Details() with { Vehicle = new VehicleDetails(null, "Ford") }, [Guid.NewGuid()], Now));
        Assert.Throws<ArgumentException>(() => Post.Create(UserId, Details() with { Vehicle = new VehicleDetails(VehicleType.Car, "Ford", "Golf") }, [Guid.NewGuid()], Now));
        Assert.Throws<ArgumentException>(() => Post.Create(UserId, Details() with { Vehicle = new VehicleDetails(VehicleType.Car, "AB12 CDE") }, [Guid.NewGuid()], Now));

        Assert.Equal("Other", VehicleCatalogue.Model("Other", "other"));
    }

    [Theory]
    [InlineData("2026-06-21T12:00:00Z", 51.5, -0.12, false)] // London, midsummer noon
    [InlineData("2026-12-21T18:00:00Z", 51.5, -0.12, true)] // London, winter evening
    [InlineData("2026-06-21T20:15:00Z", 57.15, -2.1, false)] // Aberdeen, 21:15 BST in June: still light
    [InlineData("2026-12-21T07:00:00Z", 53.48, -2.24, true)] // Manchester, 07:00 in December: before sunrise
    public void Daylight_follows_the_sun(string at, double lat, double lng, bool dark)
    {
        Assert.Equal(dark, Daylight.IsDark(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), (decimal)lat, (decimal)lng));
    }
}
