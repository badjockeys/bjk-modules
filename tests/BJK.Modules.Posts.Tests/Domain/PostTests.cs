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
}
