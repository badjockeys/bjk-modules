using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Media.Tests.Processing;
using BJK.Modules.Media.UseCases.Share;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

/// <summary>Vertical copies of videos for TikTok, Reels, Shorts and X (owner, 23 Oct 2026).</summary>
public sealed class ShareCopyTests : IDisposable
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IShareCopyStore _copies = Substitute.For<IShareCopyStore>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-share-tests-").FullName;

    public ShareCopyTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _storage.CreateOriginalDownloadUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri($"https://originals.example.com/{ci.ArgAt<string>(0)}?download={ci.ArgAt<string>(2)}"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Asking_for_a_copy_queues_the_worker_and_says_it_is_rendering()
    {
        var video = ReadyVideo(UserId);

        var dto = await Request().Handle(new RequestShareCopyCommand(video.Id), Ct);

        Assert.Equal(ShareCopyState.Rendering, dto.State);
        _copies.Received(1).Add(Arg.Is<ShareCopy>(c => c.MediaId == video.Id && c.UserId == UserId && c.Status == ShareCopyStatus.Rendering));
        await _copies.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(
            Arg.Is<MediaProcessingJob>(j => j.MediaId == video.Id && j.Step == MediaJobStep.ShareCopy && j.Revision == video.Revision),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_made_copy_is_returned_with_a_download_link_instead_of_making_another()
    {
        var video = ReadyVideo(UserId);
        var copy = ShareCopy.Start(video, Now.AddMinutes(-5));
        copy.MarkReady(MediaKeys.ShareVideo(UserId, video.Id), Now.AddMinutes(-4));
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(copy);

        var dto = await Request().Handle(new RequestShareCopyCommand(video.Id), Ct);

        Assert.Equal(ShareCopyState.Ready, dto.State);
        Assert.Contains("share/vertical.mp4", dto.DownloadUrl!.ToString(), StringComparison.Ordinal);
        Assert.Contains("download=bad-jockeys-clip.mp4", dto.DownloadUrl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(Now.AddMinutes(15), dto.LinkExpiresAt);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_copy_being_made_is_not_started_twice_but_a_lost_one_is()
    {
        var video = ReadyVideo(UserId);
        var copy = ShareCopy.Start(video, Now.AddMinutes(-2));
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(copy);

        Assert.Equal(ShareCopyState.Rendering, (await Request().Handle(new RequestShareCopyCommand(video.Id), Ct)).State);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());

        var lost = ShareCopy.Start(video, Now - ShareCopy.StaleAfter - TimeSpan.FromMinutes(1));
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(lost);
        await Request().Handle(new RequestShareCopyCommand(video.Id), Ct);
        Assert.Equal(Now, lost.RequestedAt);
        await _queue.Received(1).EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_and_expired_copies_start_again()
    {
        var video = ReadyVideo(UserId);
        var failed = ShareCopy.Start(video, Now.AddMinutes(-3));
        failed.MarkFailed();
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(failed);
        Assert.Equal(ShareCopyState.Failed, (await Get().Handle(new GetShareCopyQuery(video.Id), Ct)).State);

        await Request().Handle(new RequestShareCopyCommand(video.Id), Ct);
        Assert.Equal(ShareCopyStatus.Rendering, failed.Status);

        var expired = ShareCopy.Start(video, Now.AddDays(-40));
        expired.MarkReady("key", Now - ShareCopy.KeptFor - TimeSpan.FromHours(1));
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(expired);
        Assert.Equal(ShareCopyState.None, (await Get().Handle(new GetShareCopyQuery(video.Id), Ct)).State);
        await Request().Handle(new RequestShareCopyCommand(video.Id), Ct);
        Assert.Equal(ShareCopyStatus.Rendering, expired.Status);
        Assert.Null(expired.Key);
    }

    [Fact]
    public async Task Other_peoples_videos_are_not_found()
    {
        var video = ReadyVideo(OtherUserId);

        await Assert.ThrowsAsync<NotFoundException>(() => Request().Handle(new RequestShareCopyCommand(video.Id), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => Get().Handle(new GetShareCopyQuery(video.Id), Ct));
    }

    [Fact]
    public async Task Photos_unfinished_videos_and_sensitive_videos_cannot_be_shared()
    {
        var photo = MediaItem.Create(UserId, "parking.jpg", "image/jpeg", Now);
        photo.MarkUploaded(1, Now, TimeSpan.FromDays(1));
        photo.MarkReady(10, null, 1600, 1200, null, Now);
        _media.GetByIdAsync(photo.Id, Arg.Any<CancellationToken>()).Returns(photo);
        var photoError = await Assert.ThrowsAsync<ValidationException>(() => Request().Handle(new RequestShareCopyCommand(photo.Id), Ct));
        Assert.Contains("Only videos", photoError.Message, StringComparison.Ordinal);

        var processing = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        _media.GetByIdAsync(processing.Id, Arg.Any<CancellationToken>()).Returns(processing);
        await Assert.ThrowsAsync<ValidationException>(() => Request().Handle(new RequestShareCopyCommand(processing.Id), Ct));

        var sensitive = ReadyVideo(UserId);
        _media.IsSensitiveAsync(sensitive.Id, Arg.Any<CancellationToken>()).Returns(true);
        var sensitiveError = await Assert.ThrowsAsync<ValidationException>(() => Request().Handle(new RequestShareCopyCommand(sensitive.Id), Ct));
        Assert.Contains("sensitive", sensitiveError.Message, StringComparison.Ordinal);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_copy_of_an_older_revision_is_out_of_date()
    {
        var video = ReadyVideo(UserId);
        var copy = ShareCopy.Start(video, Now.AddMinutes(-5));
        copy.MarkReady("key", Now.AddMinutes(-4));
        _copies.GetAsync(video.Id, Arg.Any<CancellationToken>()).Returns(copy);
        var newer = ReadyVideo(UserId, revision: 1, id: video.Id);

        Assert.Equal(ShareCopyState.None, (await Get().Handle(new GetShareCopyQuery(newer.Id), Ct)).State);
    }

    [Fact]
    public async Task The_share_video_is_vertical_silent_and_ends_with_the_end_card()
    {
        FfmpegTestSupport.RequireFfmpeg();
        var transcoder = new FfmpegTranscoder(
            Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
            NullLogger<FfmpegTranscoder>.Instance);
        var input = FfmpegTestSupport.Make(
            Path.Combine(_folder, "landscape.mp4"),
            "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        var (watermark, endCard) = await ShareVideoAssets.WriteToAsync(_folder, Ct);
        var output = Path.Combine(_folder, "vertical.mp4");

        await transcoder.CreateShareVideoAsync(input, watermark, endCard, output, Ct);

        var probe = await transcoder.ProbeAsync(output, Ct);
        Assert.Equal(1080, probe.Width);
        Assert.Equal(1920, probe.Height);
        Assert.InRange(probe.DurationSeconds!.Value, 4.3m, 4.8m);
        Assert.DoesNotContain("codec_type=audio", await FfmpegTestSupport.Ffprobe(output), StringComparison.Ordinal);
    }

    private RequestShareCopyCommandHandler Request() =>
        new(_media, _copies, _queue, _storage, _caller, _clock, NullLogger<RequestShareCopyCommandHandler>.Instance);

    private GetShareCopyQueryHandler Get() => new(_media, _copies, _storage, _caller, _clock);

    private MediaItem ReadyVideo(Guid ownerId, int revision = 0, Guid? id = null)
    {
        var item = MediaItem.Create(ownerId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(1, Now, TimeSpan.FromDays(1));
        item.MarkReady(10, 30m, 1280, 720, null, Now);
        if (revision > 0 || id is not null)
        {
            typeof(MediaItem).GetProperty(nameof(MediaItem.Revision))!.SetValue(item, revision);
            typeof(MediaItem).GetProperty(nameof(MediaItem.Id))!.SetValue(item, id ?? item.Id);
        }

        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }
}
