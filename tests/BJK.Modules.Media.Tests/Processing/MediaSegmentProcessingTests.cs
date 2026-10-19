using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>
/// Longer videos split into segment jobs: planning, repeated and parallel deliveries, joining once, and the outcome.
/// The ffmpeg work is stubbed here; <see cref="SegmentedVideoTests"/> runs it for real.
/// </summary>
public sealed class MediaSegmentProcessingTests
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaTranscoder _transcoder = Substitute.For<IMediaTranscoder>();
    private readonly IMediaRedactor _redactor = Substitute.For<IMediaRedactor>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly InMemoryVideoJobStore _jobs = new();
    private readonly List<MediaProcessingJob> _queued = [];
    private readonly Dictionary<int, IReadOnlyList<string>> _segmentReasons = [];

    public MediaSegmentProcessingTests()
    {
        _storage.DownloadOriginalAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Stream>().WriteAsync(new byte[64], 0, 64, ci.Arg<CancellationToken>()));
        _queue.EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _queued.Add(ci.Arg<MediaProcessingJob>());
                return Task.CompletedTask;
            });
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 12m, 1920, 1080, 30, 0));
        _redactor.RedactVideoSegmentAsync(Arg.Any<string>(), Arg.Any<VideoSegmentPlan>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(4), new byte[100]);
                var index = ci.ArgAt<int>(2);
                return Task.FromResult(new RedactionResult(1, index == 1 ? 1 : 0, _segmentReasons.GetValueOrDefault(index, []), 150, 30, TimeSpan.FromSeconds(2)));
            });
        _transcoder.ConcatenateVideoAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(2), new byte[999]);
                return Task.CompletedTask;
            });
        _transcoder.CreateVideoThumbnailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(1), new byte[10]);
                return Task.CompletedTask;
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaProcessor Processor(MediaRedactionOptions? redaction = null) => new(
        _media,
        _jobs,
        _queue,
        _storage,
        _transcoder,
        _redactor,
        Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions { WorkingDirectory = Path.GetTempPath(), SegmentSeconds = 5 }),
        Microsoft.Extensions.Options.Options.Create(redaction ?? new MediaRedactionOptions()),
        new FixedTimeProvider(Now),
        NullLogger<MediaProcessor>.Instance);

    private MediaItem Video()
    {
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(64, Now, TimeSpan.FromDays(90));
        item.TakeStatusChanges();
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }

    private static MediaProcessingJob Job(MediaItem item) =>
        new(item.Id, item.UserId, item.MediaType, item.OriginalKey, item.OriginalContentType);

    private List<MediaProcessingJob> Take(MediaJobStep step)
    {
        var jobs = _queued.Where(j => j.Step == step).ToList();
        _queued.RemoveAll(j => j.Step == step);
        return jobs;
    }

    [Theory]
    [InlineData(12.0, 30.0, 3, 150)]
    [InlineData(30.0, 30.0, 6, 150)]
    [InlineData(29.97, 29.97, 6, 150)]
    [InlineData(5.0, 30.0, 1, 150)]
    [InlineData(5.5, 30.0, 1, 150)]
    [InlineData(10.5, 30.0, 2, 150)]
    [InlineData(11.5, 30.0, 3, 150)]
    [InlineData(20.0, 25.0, 4, 125)]
    public void Videos_longer_than_a_segment_are_split_into_5_second_parts(double seconds, double fps, int segments, int framesPerSegment)
    {
        var plan = VideoSegmentPlan.For(seconds, fps, 0, 5, 1.0);

        Assert.Equal(segments, plan.SegmentCount);
        Assert.Equal(framesPerSegment, plan.FramesPerSegment);
        Assert.Equal((int)Math.Ceiling(fps), plan.OverlapFrames);
        Assert.Equal(0, plan.CoreStart(0));
        Assert.Null(plan.CoreEnd(segments - 1));
        if (segments > 1)
        {
            Assert.Equal(framesPerSegment, plan.CoreEnd(0));
            Assert.Equal(framesPerSegment, plan.CoreStart(1));
        }
    }

    [Fact]
    public void Splitting_can_be_turned_off()
    {
        Assert.False(VideoSegmentPlan.For(30, 30, 0, segmentSeconds: 0, 1.0).IsSplit);
    }

    [Fact]
    public void Each_segment_decodes_its_own_part_plus_the_overlap_either_side()
    {
        var plan = VideoSegmentPlan.For(12, 30, 0, 5, 1.0);

        Assert.Equal(new FrameRange(0, 0, 150, 179), MediaRedactor.SegmentRange(plan, 0));
        Assert.Equal(new FrameRange(120, 150, 300, 329), MediaRedactor.SegmentRange(plan, 1));
        Assert.Equal(new FrameRange(270, 300, null, null), MediaRedactor.SegmentRange(plan, 2));
    }

    [Fact]
    public async Task A_12_second_video_is_planned_as_three_segment_jobs()
    {
        var item = Video();

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.Processing, item.Status);
        Assert.Equal(12m, item.DurationSeconds);
        Assert.NotNull(item.Sha256);
        Assert.Equal(3, _jobs.Job(item.Id)!.SegmentCount);
        var segments = Take(MediaJobStep.Segment);
        Assert.Equal(new[] { 0, 1, 2 }, segments.Select(j => j.Segment!.Value));
        Assert.All(segments, j => Assert.Equal($"{item.Id:D}/r0/s{j.Segment}", j.MessageId));
        await _redactor.DidNotReceiveWithAnyArgs().RedactVideoAsync(default!, default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Short_clips_and_photos_are_not_split()
    {
        var item = Video();
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 4.8m, 1920, 1080, 30, 0));
        _redactor.RedactVideoAsync(Arg.Any<string>(), Arg.Any<ProbeResult>(), Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(3), new byte[50]);
                return Task.FromResult(new RedactionResult(0, 0, [], 144, 29, TimeSpan.FromSeconds(1)));
            });

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Empty(_queued);
        Assert.Null(_jobs.Job(item.Id));
    }

    [Fact]
    public async Task Segments_are_joined_once_when_all_are_done_even_with_repeat_deliveries()
    {
        var item = Video();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        var segments = Take(MediaJobStep.Segment);

        // Segment 0, then segment 1 twice (redelivered after a worker restart).
        await Processor().ProcessAsync(segments[0], isLastAttempt: false, Ct);
        await Processor().ProcessAsync(segments[1], isLastAttempt: false, Ct);
        await Processor().ProcessAsync(segments[1], isLastAttempt: false, Ct, isRedelivery: true);
        Assert.Empty(Take(MediaJobStep.Assemble));

        // The plan job delivered again only re-queues the unfinished segment.
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        Assert.Equal(new[] { 2 }, Take(MediaJobStep.Segment).Select(j => j.Segment!.Value));

        await Processor().ProcessAsync(segments[2], isLastAttempt: false, Ct);
        var assemble = Assert.Single(Take(MediaJobStep.Assemble));
        Assert.Equal(1, _jobs.Claims);

        // Each segment was blurred exactly once and stored privately.
        await _redactor.ReceivedWithAnyArgs(3).RedactVideoSegmentAsync(default!, default!, default, default!, default!, Ct);
        foreach (var index in new[] { 0, 1, 2 })
        {
            await _storage.Received(1).UploadPrivateAsync(
                $"{UserId:D}/{item.Id:D}/segments/r0/{index:D3}.mp4", Arg.Any<string>(), "video/mp4", Arg.Any<CancellationToken>());
        }

        // The assemble job, delivered twice: the second finds the media finished.
        await Processor().ProcessAsync(assemble, isLastAttempt: false, Ct);
        await Processor().ProcessAsync(assemble, isLastAttempt: false, Ct, isRedelivery: true);

        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Equal($"{UserId:D}/{item.Id:D}/720p.mp4", item.PlaybackKey);
        Assert.Equal((3, 1), (item.FacesBlurred, item.PlatesBlurred));
        Assert.NotNull(_jobs.Job(item.Id)!.AssembledAt);
        await _transcoder.Received(1).ConcatenateVideoAsync(
            Arg.Is<IReadOnlyList<string>>(p => p.Select(Path.GetFileName).SequenceEqual(new[] { "000.mp4", "001.mp4", "002.mp4" })),
            Arg.Is<string?>(p => p == null),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _storage.Received(1).UploadPublicAsync(item.PlaybackKey!, Arg.Any<string>(), "video/mp4", Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteOriginalAsync($"{UserId:D}/{item.Id:D}/segments/r0/001.mp4", Arg.Any<CancellationToken>());
        Assert.Single(item.TakeStatusChanges(), c => c.Status == MediaStatus.Ready);
    }

    [Fact]
    public async Task A_redelivered_segment_whose_assembly_claim_was_lost_queues_it_again()
    {
        var item = Video();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        var segments = Take(MediaJobStep.Segment);
        foreach (var segment in segments)
        {
            await Processor().ProcessAsync(segment, isLastAttempt: false, Ct);
        }

        // Pretend the worker that claimed the assembly died before its job reached the queue.
        Assert.Single(Take(MediaJobStep.Assemble));

        await Processor().ProcessAsync(segments[2], isLastAttempt: false, Ct, isRedelivery: true);

        Assert.Single(Take(MediaJobStep.Assemble));
        await _redactor.ReceivedWithAnyArgs(3).RedactVideoSegmentAsync(default!, default!, default, default!, default!, Ct);
    }

    [Fact]
    public async Task An_uncertain_segment_sends_the_whole_video_to_review()
    {
        var item = Video();
        _segmentReasons[1] = ["1 detection(s) had low confidence."];
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        foreach (var segment in Take(MediaJobStep.Segment))
        {
            await Processor().ProcessAsync(segment, isLastAttempt: false, Ct);
        }

        await Processor().ProcessAsync(Assert.Single(Take(MediaJobStep.Assemble)), isLastAttempt: false, Ct);

        Assert.Equal(MediaStatus.NeedsReview, item.Status);
        Assert.Equal("1 detection(s) had low confidence.", item.ReviewReason);
        await _storage.Received(1).UploadPrivateAsync($"{UserId:D}/{item.Id:D}/review/720p.mp4", Arg.Any<string>(), "video/mp4", Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().UploadPublicAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Segments_of_media_deleted_meanwhile_are_skipped()
    {
        var item = Video();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        item.Delete(Now);

        foreach (var segment in Take(MediaJobStep.Segment))
        {
            Assert.Equal(ProcessingOutcome.Completed, await Processor().ProcessAsync(segment, isLastAttempt: false, Ct));
        }

        await _redactor.DidNotReceiveWithAnyArgs().RedactVideoSegmentAsync(default!, default!, default, default!, default!, Ct);
    }

    [Fact]
    public async Task A_segment_that_keeps_failing_fails_the_media_on_its_last_attempt()
    {
        var item = Video();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        var segment = Take(MediaJobStep.Segment)[1];
        _redactor.RedactVideoSegmentAsync(Arg.Any<string>(), Arg.Any<VideoSegmentPlan>(), 1, Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("too slow"));

        Assert.Equal(ProcessingOutcome.Retry, await Processor().ProcessAsync(segment, isLastAttempt: false, Ct));
        Assert.Equal(MediaStatus.Processing, item.Status);

        Assert.Equal(ProcessingOutcome.GaveUp, await Processor().ProcessAsync(segment, isLastAttempt: true, Ct, isRedelivery: true));
        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Equal(2, _jobs.Job(item.Id)!.Segments[1].Attempts);
        Assert.Single(item.TakeStatusChanges(), c => c.Status == MediaStatus.Failed);
    }

    [Fact]
    public async Task Jobs_for_an_older_revision_are_ignored()
    {
        var item = Video();
        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);
        var stale = Take(MediaJobStep.Segment)[0] with { Revision = 7 };

        Assert.Equal(ProcessingOutcome.Completed, await Processor().ProcessAsync(stale, isLastAttempt: false, Ct));
        await _redactor.DidNotReceiveWithAnyArgs().RedactVideoSegmentAsync(default!, default!, default, default!, default!, Ct);
    }

    [Fact]
    public async Task With_audio_kept_the_original_soundtrack_is_added_when_joining()
    {
        var item = Video();
        var options = new MediaRedactionOptions { RemoveAudio = false };
        await Processor(options).ProcessAsync(Job(item), isLastAttempt: false, Ct);
        foreach (var segment in Take(MediaJobStep.Segment))
        {
            await Processor(options).ProcessAsync(segment, isLastAttempt: false, Ct);
        }

        await Processor(options).ProcessAsync(Assert.Single(Take(MediaJobStep.Assemble)), isLastAttempt: false, Ct);

        await _transcoder.Received(1).ConcatenateVideoAsync(
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Is<string?>(p => p != null && p.EndsWith("original.mp4")),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
