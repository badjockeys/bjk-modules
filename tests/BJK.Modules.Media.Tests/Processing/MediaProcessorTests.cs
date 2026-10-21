using System.Security.Cryptography;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Safety;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Processing;

public sealed class MediaProcessorTests
{
    private static readonly byte[] OriginalBytes = "pretend this is a dashcam video"u8.ToArray();

    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaTranscoder _transcoder = Substitute.For<IMediaTranscoder>();
    private readonly IMediaRedactor _redactor = Substitute.For<IMediaRedactor>();
    private readonly List<(string Key, string Type, long Size)> _uploads = [];
    private readonly List<(string Key, string Type)> _privateUploads = [];
    private RedactionResult _redaction = new(2, 1, [], 300, 60, TimeSpan.FromSeconds(3));

    public MediaProcessorTests()
    {
        _storage.DownloadOriginalAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Stream>().WriteAsync(OriginalBytes, 0, OriginalBytes.Length, ci.Arg<CancellationToken>()));
        _storage.UploadPublicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _uploads.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(2), new FileInfo(ci.ArgAt<string>(1)).Length));
                return Task.CompletedTask;
            });
        _storage.UploadPrivateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _privateUploads.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(2)));
                return Task.CompletedTask;
            });
        _redactor.RedactVideoAsync(Arg.Any<string>(), Arg.Any<ProbeResult>(), Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(3), new byte[1234]);
                return Task.FromResult(_redaction);
            });
        _redactor.RedactImageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(2), new byte[99]);
                return Task.FromResult(_redaction with { Frames = 1, FramesAnalysed = 1 });
            });
        _transcoder.CreateVideoThumbnailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(1), new byte[56]);
                return Task.CompletedTask;
            });
        _transcoder.ConvertImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => WriteOutputs(ci.ArgAt<string>(1), ci.ArgAt<string>(2)));
        _transcoder.CreateFeedImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(1), new byte[77]);
                return Task.CompletedTask;
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task WriteOutputs(string playback, string thumbnail)
    {
        File.WriteAllBytes(playback, new byte[1234]);
        File.WriteAllBytes(thumbnail, new byte[56]);
        return Task.CompletedTask;
    }

    private MediaItem ProcessingItem(string contentType = "video/mp4")
    {
        var item = MediaItem.Create(UserId, "clip", contentType, Now);
        item.MarkUploaded(OriginalBytes.Length, Now, TimeSpan.FromDays(90));
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }

    // These tests cover photos and videos processed in one job; MediaSegmentProcessingTests covers splitting.
    private MediaProcessor Processor(IKnownImageList? known = null, IKnownImageRepository? knownRepository = null) => new(
        _media,
        new InMemoryVideoJobStore(),
        Substitute.For<IMediaJobQueue>(),
        _storage,
        _transcoder,
        _redactor,
        Microsoft.Extensions.Options.Options.Create(new MediaOptions { MaxVideoDurationSeconds = 300 }),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions { WorkingDirectory = Path.GetTempPath(), SegmentSeconds = 0 }),
        Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
        new FixedTimeProvider(Now),
        NullLogger<MediaProcessor>.Instance,
        knownImages: known,
        knownImageRepository: knownRepository);

    private static MediaProcessingJob Job(MediaItem item) =>
        new(item.Id, item.UserId, item.MediaType, item.OriginalKey, item.OriginalContentType);

    [Fact]
    public async Task A_video_is_converted_published_and_marked_ready()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 42.5m, 1920, 1080));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Equal($"{UserId:D}/{item.Id:D}/720p.mp4", item.PlaybackKey);
        Assert.Equal($"{UserId:D}/{item.Id:D}/thumbnail.jpg", item.ThumbnailKey);
        Assert.Equal(1234, item.PlaybackSizeBytes);
        Assert.Equal(42.5m, item.DurationSeconds);
        Assert.Equal((1920, 1080), (item.Width, item.Height));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(OriginalBytes)), item.Sha256);
        Assert.Equal(1, item.ProcessingAttempts);
        Assert.Equal(
            new[] { (item.PlaybackKey!, "video/mp4", 1234L), (item.ThumbnailKey!, "image/jpeg", 56L) },
            _uploads.Select(u => (u.Key, u.Type, u.Size)));
        await _storage.Received(1).DownloadOriginalAsync(item.OriginalKey, Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    // Known images

    [Fact]
    public async Task An_exact_copy_of_a_known_image_is_blocked_before_processing()
    {
        var item = ProcessingItem("image/jpeg");
        var hit = new KnownImageHit(Guid.NewGuid(), KnownHashKind.Sha256, Convert.ToHexStringLower(SHA256.HashData(OriginalBytes)), "Test", null, null);
        var known = Substitute.For<IKnownImageList>();
        known.MatchFile(Convert.ToHexStringLower(SHA256.HashData(OriginalBytes)), Convert.ToHexStringLower(MD5.HashData(OriginalBytes))).Returns(hit);
        var repository = Substitute.For<IKnownImageRepository>();

        var outcome = await Processor(known, repository).ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Failed, outcome);
        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Equal(MediaProcessor.BlockedReason, item.FailureReason);
        Assert.Empty(_uploads);
        Assert.Empty(_privateUploads);
        await _transcoder.DidNotReceiveWithAnyArgs().ProbeAsync(default!, Ct);
        await _storage.Received(1).DeleteOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>());
        repository.Received(1).Add(Arg.Is<KnownImageMatch>(m => m.MediaId == item.Id && m.UserId == UserId && m.HashId == hit.HashId));
    }

    [Fact]
    public async Task A_frame_matching_a_known_image_blocks_the_media_and_nothing_is_published()
    {
        var item = ProcessingItem("image/jpeg");
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 0.04m, 800, 600));
        var hashId = Guid.NewGuid();
        var hit = new KnownImageHit(hashId, KnownHashKind.Pdq, new string('a', 64), "Test", "test-1", 4);
        _redaction = _redaction with { ReviewReasons = [hit.ToReason(), "1 detection(s) had low confidence."] };
        var repository = Substitute.For<IKnownImageRepository>();
        repository.GetHashAsync(hashId, Arg.Any<CancellationToken>())
            .Returns(new KnownImageHash(KnownHashKind.Pdq, new string('a', 64), "Test", "test-1", Now));

        var outcome = await Processor(Substitute.For<IKnownImageList>(), repository).ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Failed, outcome);
        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Empty(_uploads);
        Assert.Empty(_privateUploads);
        repository.Received(1).Add(Arg.Is<KnownImageMatch>(m => m.Distance == 4 && m.Source == "Test" && m.Reference == "test-1"));
    }

    [Fact]
    public async Task Nudity_and_perceptual_hashes_are_recorded()
    {
        var item = ProcessingItem("image/jpeg");
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 0.04m, 800, 600));
        _redaction = _redaction with { NudityScore = 0.0123456, PerceptualHashes = [new string('b', 64)] };

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(0.0123, item.NudityScore);
        Assert.Equal(new string('b', 64), item.PerceptualHashes);
    }

    [Fact]
    public async Task A_photo_becomes_webp_without_a_duration()
    {
        var item = ProcessingItem("image/jpeg");
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 0.04m, 4032, 3024));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Null(item.DurationSeconds);
        Assert.Equal(
            new[] { ($"{UserId:D}/{item.Id:D}/image.webp", 1234L), ($"{UserId:D}/{item.Id:D}/thumbnail.webp", 56L), ($"{UserId:D}/{item.Id:D}/feed.webp", 77L) },
            _uploads.Select(u => (u.Key, u.Size)));
        Assert.All(_uploads, u => Assert.Equal("image/webp", u.Type));
        Assert.EndsWith("/image.webp", item.PlaybackKey);
        Assert.Equal($"{UserId:D}/{item.Id:D}/feed.webp", item.FeedKey);
        await _transcoder.Received(1).CreateFeedImageAsync(Arg.Is<string>(p => p.EndsWith("redacted.ppm")), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _redactor.DidNotReceiveWithAnyArgs().RedactVideoAsync(default!, default!, default!, default!, Ct);
        await _transcoder.Received(1).ConvertImageAsync(Arg.Is<string>(p => p.EndsWith("redacted.ppm")), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_video_that_is_too_long_fails_without_encoding()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 301m, 1920, 1080));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Failed, outcome);
        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Equal("Videos can be up to 5 minutes long, and this one is 5 minutes 1 second. Trim it to 5 minutes or less and upload it again.", item.FailureReason);
        await _redactor.DidNotReceiveWithAnyArgs().RedactVideoAsync(default!, default!, default!, default!, Ct);
        Assert.Empty(_uploads);
    }

    [Fact]
    public async Task An_unreadable_file_fails_straight_away()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new UnreadableMediaException("moov atom not found"));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Failed, outcome);
        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.DoesNotContain("moov", item.FailureReason);
    }

    [Fact]
    public async Task A_file_with_no_picture_fails()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(false, 30m, null, null));

        Assert.Equal(ProcessingOutcome.Failed, await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct));
        Assert.Equal(MediaStatus.Failed, item.Status);
    }

    [Fact]
    public async Task A_temporary_problem_is_retried_and_leaves_the_media_processing()
    {
        var item = ProcessingItem();
        _storage.DownloadOriginalAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("storage unavailable"));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Retry, outcome);
        Assert.Equal(MediaStatus.Processing, item.Status);
    }

    [Fact]
    public async Task On_the_last_attempt_a_temporary_problem_marks_it_failed()
    {
        var item = ProcessingItem();
        _storage.DownloadOriginalAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("storage unavailable"));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: true, Ct);

        Assert.Equal(ProcessingOutcome.GaveUp, outcome);
        Assert.Equal(MediaStatus.Failed, item.Status);
    }

    [Fact]
    public async Task Media_that_is_no_longer_processing_is_skipped()
    {
        var item = ProcessingItem();
        item.Delete(Now);

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        await _storage.DidNotReceiveWithAnyArgs().DownloadOriginalAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Missing_media_is_skipped()
    {
        var outcome = await Processor().ProcessAsync(
            new MediaProcessingJob(Guid.NewGuid(), UserId, MediaType.Video, "x/y/original.mp4", "video/mp4"),
            isLastAttempt: false,
            Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
    }

    [Fact]
    public async Task If_the_owner_deletes_it_meanwhile_the_published_files_are_removed()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 10m, 1280, 720));
        var saves = 0;
        _media.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            ++saves == 2 ? Task.FromException(new DbUpdateConcurrencyException("deleted")) : Task.CompletedTask);

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        await _storage.Received(1).DeletePublicAsync(
            Arg.Is<IEnumerable<string>>(keys => keys.Count() == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Temporary_files_are_removed_afterwards()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 10m, 1280, 720));

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "bjk-media", item.Id.ToString("N"))));
    }

    [Fact]
    public async Task Blur_counts_are_recorded_and_the_thumbnail_comes_from_the_blurred_video()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 10m, 1280, 720, 30));

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Equal((2, 1), (item.FacesBlurred, item.PlatesBlurred));
        await _transcoder.Received(1).CreateVideoThumbnailAsync(
            Arg.Is<string>(p => p.EndsWith("720p.mp4")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_detection_is_unsure_the_blurred_files_stay_private_and_it_needs_review()
    {
        var item = ProcessingItem();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 10m, 1280, 720));
        _redaction = _redaction with { ReviewReasons = ["The NumberPlate detection model is not configured (Media:Redaction:PlateModelPath)."] };

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.NeedsReview, item.Status);
        Assert.Contains("not configured", item.ReviewReason);
        Assert.Null(item.PlaybackKey);
        Assert.Null(item.ThumbnailKey);
        Assert.Empty(_uploads);
        Assert.Equal(
            new[] { ($"{UserId:D}/{item.Id:D}/review/720p.mp4", "video/mp4"), ($"{UserId:D}/{item.Id:D}/review/thumbnail.jpg", "image/jpeg") },
            _privateUploads);
    }

    [Fact]
    public async Task Manual_regions_are_applied_and_the_result_goes_back_to_review()
    {
        var item = ProcessingItem("image/jpeg");
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, null, 4000, 3000));
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        Assert.Equal(MediaStatus.Ready, item.Status);

        var region = new ManualRedaction(0.1, 0.2, 0.3, 0.1);
        item.AddManualRedactions([region], Now);
        _uploads.Clear();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(MediaStatus.NeedsReview, item.Status);
        Assert.Contains("moderator added 1 blur region", item.ReviewReason);
        await _redactor.Received(1).RedactImageAsync(
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<ManualRedaction>>(r => r.Count == 1 && r[0] == region),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(_privateUploads, u => u.Key == $"{UserId:D}/{item.Id:D}/review/image-r1.webp");
        Assert.Contains(_privateUploads, u => u.Key == $"{UserId:D}/{item.Id:D}/review/feed-r1.webp");
        Assert.Null(item.FeedKey);
        Assert.Empty(_uploads);
    }
}
