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
    public void Location_is_rounded_to_six_decimal_places()
    {
        var details = Details() with { Latitude = 53.12345678m, Longitude = -2.98765432m };

        var post = Post.Create(UserId, details, [Guid.NewGuid()], Now);

        Assert.Equal(53.123457m, post.Latitude);
        Assert.Equal(-2.987654m, post.Longitude);
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
