using BJK.Modules.Media.Domain;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Domain;

public sealed class MediaItemTests
{
    [Fact]
    public void Create_files_the_original_under_user_and_media_ids_with_our_own_file_name()
    {
        var item = MediaItem.Create(UserId, "../20261017_JohnSmith_AB12CDE.MOV", "video/quicktime", Now);

        Assert.Equal(MediaType.Video, item.MediaType);
        Assert.Equal(MediaStatus.Uploading, item.Status);
        Assert.Equal($"{UserId:D}/{item.Id:D}/original.mov", item.OriginalKey);
        Assert.Equal("20261017_JohnSmith_AB12CDE.MOV", item.OriginalFileName);
    }

    [Theory]
    [InlineData("image/heic", MediaType.Image, "heic")]
    [InlineData("image/jpeg", MediaType.Image, "jpg")]
    [InlineData("video/mp4", MediaType.Video, "mp4")]
    public void Create_derives_type_and_extension_from_content_type(string contentType, MediaType type, string extension)
    {
        var item = MediaItem.Create(UserId, "file", contentType, Now);

        Assert.Equal(type, item.MediaType);
        Assert.EndsWith($"/original.{extension}", item.OriginalKey);
    }

    [Fact]
    public void Create_rejects_unsupported_types()
    {
        Assert.Throws<ArgumentException>(() => MediaItem.Create(UserId, "doc.pdf", "application/pdf", Now));
    }

    [Fact]
    public void MarkUploaded_starts_processing_and_schedules_original_deletion()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);

        item.MarkUploaded(1234, Now.AddMinutes(2), TimeSpan.FromDays(90));

        Assert.Equal(MediaStatus.Processing, item.Status);
        Assert.Equal(1234, item.OriginalSizeBytes);
        Assert.Equal(Now.AddMinutes(2).AddDays(90), item.OriginalDeletesAt);
    }

    [Fact]
    public void MarkReady_sets_public_keys_for_videos()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(1234, Now, TimeSpan.FromDays(90));

        item.MarkReady(999, 42.5m, 1280, 720, null, Now.AddMinutes(1));

        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Equal($"{UserId:D}/{item.Id:D}/720p.mp4", item.PlaybackKey);
        Assert.Equal($"{UserId:D}/{item.Id:D}/thumbnail.jpg", item.ThumbnailKey);
    }

    [Fact]
    public void MarkReady_sets_public_keys_for_photos()
    {
        var item = MediaItem.Create(UserId, "parking.heic", "image/heic", Now);
        item.MarkUploaded(1234, Now, TimeSpan.FromDays(90));

        item.MarkReady(999, null, 1600, 1200, null, Now);

        Assert.EndsWith("/image.webp", item.PlaybackKey);
        Assert.EndsWith("/thumbnail.webp", item.ThumbnailKey);
    }

    [Fact]
    public void MarkUploaded_twice_is_refused()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(1, Now, TimeSpan.FromDays(90));

        Assert.Throws<InvalidOperationException>(() => item.MarkUploaded(1, Now, TimeSpan.FromDays(90)));
    }

    [Fact]
    public void Failure_reason_is_truncated_to_fit_the_column()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);

        item.MarkFailed(new string('x', 900), Now);

        Assert.Equal(MediaItem.FailureReasonMaxLength, item.FailureReason!.Length);
    }
}
