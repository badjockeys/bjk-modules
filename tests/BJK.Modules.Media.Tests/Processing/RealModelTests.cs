using System.Diagnostics;
using System.Globalization;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static BJK.Modules.Media.Tests.Processing.FfmpegTestSupport;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>
/// The real YuNet and LPD-YuNet models with ONNX Runtime. Run when <c>BJK_REDACTION_MODELS</c> names a folder filled
/// by <c>scripts/download-redaction-models.sh &lt;folder&gt; --test-images</c> (CI does this); skipped otherwise.
/// The timing tests run only when <c>BJK_REDACTION_PERF_SECONDS</c> (the clip length) is set as well.
/// </summary>
[Collection(nameof(RealModelTests))]
public sealed class RealModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-model-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string Models()
    {
        var folder = Environment.GetEnvironmentVariable("BJK_REDACTION_MODELS");
        if (string.IsNullOrEmpty(folder) || !File.Exists(Path.Combine(folder, "face_detection_yunet_2026may.onnx")))
        {
            Assert.Skip("BJK_REDACTION_MODELS isn't set; run scripts/download-redaction-models.sh to fetch the models.");
        }

        RequireFfmpeg();
        return folder;
    }

    private static IOptions<MediaRedactionOptions> Options(string models) => Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions
    {
        FaceModelPath = Path.Combine(models, "face_detection_yunet_2026may.onnx"),
        PlateModelPath = Path.Combine(models, "license_plate_detection_lpd_yunet_2023mar.onnx"),
    });

    [Fact]
    public async Task YuNet_finds_the_faces_in_a_crowd_photo()
    {
        var models = Models();
        using var detector = new YuNetFaceDetector(Options(models), NullLogger<YuNetFaceDetector>.Instance);
        var frame = Assert.Single(await Frames(Path.Combine(models, "test-faces.jpg")));

        Assert.True(detector.IsAvailable, detector.UnavailableReason);
        var faces = detector.Detect(frame, 0.5f);

        // OpenCV Zoo's "largest selfie": a crowd with a couple of hundred faces, many under 10 pixels wide.
        Assert.True(faces.Count(f => f.Confidence >= 0.8f) >= 50, $"Found {faces.Count} faces.");
        Assert.All(faces, f => Assert.InRange(f.Width, 3, 600));
    }

    [Fact]
    public async Task LPD_YuNet_finds_the_plate_on_a_car()
    {
        var models = Models();
        using var detector = new LpdYuNetPlateDetector(Options(models), NullLogger<LpdYuNetPlateDetector>.Instance);
        var frame = Assert.Single(await Frames(Path.Combine(models, "test-plate.jpg")));

        var plates = detector.Detect(frame, 0.5f);

        var plate = Assert.Single(plates, p => p.Confidence >= 0.8f);
        Assert.InRange(plate.Width / plate.Height, 1.5f, 8f);
    }

    private static int PerfSeconds()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("BJK_REDACTION_PERF_SECONDS"), out var seconds) || seconds <= 0)
        {
            Assert.Skip("Timing runs only when BJK_REDACTION_PERF_SECONDS is set.");
        }

        return seconds;
    }

    [Fact]
    public async Task Detector_cost_per_720p_frame_is_measured()
    {
        var models = Models();
        PerfSeconds();
        var frame = Assert.Single(await Frames(Make(
            Path.Combine(_folder, "frame.png"),
            "-i", Path.Combine(models, "test-faces.jpg"), "-vf", "scale=1280:720", "-frames:v", "1")));
        var options = Options(models);
        var noTiles = Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions
        {
            PlateModelPath = options.Value.PlateModelPath,
            PlateTiles = false,
        });
        using var faces = new YuNetFaceDetector(options, NullLogger<YuNetFaceDetector>.Instance);
        using var plates = new LpdYuNetPlateDetector(options, NullLogger<LpdYuNetPlateDetector>.Instance);
        using var platesWhole = new LpdYuNetPlateDetector(noTiles, NullLogger<LpdYuNetPlateDetector>.Instance);

        var lines = new List<string>();
        foreach (var (name, detector) in new (string, IRedactionDetector)[] { ("faces", faces), ("plates+tiles", plates), ("plates whole frame", platesWhole) })
        {
            detector.Detect(frame, 0.5f);
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                detector.Detect(frame, 0.5f);
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {timer.Elapsed.TotalMilliseconds / 10:0}ms"));
        }

        await Report($"Detector cost per 720p frame ({Environment.ProcessorCount} CPUs): {string.Join(", ", lines)}.");
    }

    private static async Task Report(string report)
    {
        TestContext.Current.SendDiagnosticMessage(report);
        foreach (var file in new[] { "GITHUB_STEP_SUMMARY", "BJK_REDACTION_TIMING_FILE" }.Select(Environment.GetEnvironmentVariable))
        {
            if (!string.IsNullOrEmpty(file))
            {
                await File.AppendAllTextAsync(file, report + Environment.NewLine, Ct);
            }
        }
    }

    [Theory]
    [InlineData("crowd")]
    [InlineData("scene")]
    public async Task A_moving_clip_is_blurred_with_the_real_models_and_timed(string kind)
    {
        var models = Models();
        var seconds = PerfSeconds();
        var duration = seconds.ToString(CultureInfo.InvariantCulture);

        // crowd: the crowd photo (a couple of hundred faces) panning across a 1080p30 frame, a worst case that also
        // exercises tracking. scene: a moving test pattern with no faces or plates, the baseline cost of every clip.
        var input = kind == "crowd"
            ? Make(
                Path.Combine(_folder, "clip.mp4"),
                "-loop", "1", "-framerate", "30", "-t", duration, "-i", Path.Combine(models, "test-faces.jpg"),
                "-vf", "scale=2400:1350,crop=1920:1080:'mod(t*40,480)':100,format=yuv420p",
                "-c:v", "libx264", "-preset", "ultrafast")
            : Make(
                Path.Combine(_folder, "clip.mp4"),
                "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate=30:duration={duration}",
                "-c:v", "libx264", "-preset", "ultrafast");
        var options = Options(models);
        using var faces = new YuNetFaceDetector(options, NullLogger<YuNetFaceDetector>.Instance);
        using var plates = new LpdYuNetPlateDetector(options, NullLogger<LpdYuNetPlateDetector>.Instance);
        var redactor = new MediaRedactor(
            [faces, plates],
            options,
            Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
            NullLogger<MediaRedactor>.Instance);

        var timer = Stopwatch.StartNew();
        var result = await redactor.RedactVideoAsync(
            input, new ProbeResult(true, seconds, 1920, 1080, 30), [], Path.Combine(_folder, "720p.mp4"), Ct);
        timer.Stop();

        if (kind == "crowd")
        {
            Assert.True(result.FacesBlurred >= 3);
        }

        Assert.Equal(seconds * 30, result.Frames);

        var perMinute = timer.Elapsed.TotalSeconds / seconds * 60;
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"Redaction timing ({kind}): {seconds}s 1080p30 clip -> 720p, {result.Frames} frames, {result.FramesAnalysed} analysed, " +
            $"{timer.Elapsed.TotalSeconds:0.0}s total, {result.DetectionTime.TotalSeconds:0.0}s detecting " +
            $"({perMinute:0.0}s per minute of video, ~{perMinute * 5:0}s for 5 minutes), " +
            $"{Environment.ProcessorCount} CPUs, {result.FacesBlurred} faces, {result.PlatesBlurred} plates tracked.");
        await Report(report);
    }

    /// <summary>
    /// End-to-end processing time of a 30-second 1080p30 clip on this machine, as one job (decode, detect, track, blur,
    /// encode, thumbnail) and split into 5-second segments (each segment timed separately, as a separate worker replica
    /// would run it, then the join and thumbnail). The parallel estimate is the probe plus the slowest segment plus the
    /// join, i.e. with one replica per segment (6 for 30 seconds) and no queue wait. Storage transfers aren't included.
    /// </summary>
    [Fact]
    public async Task A_30_second_1080p_clip_is_timed_in_one_job_and_in_segments()
    {
        var models = Models();
        PerfSeconds();
        const int seconds = 30;
        var input = Make(
            Path.Combine(_folder, "clip30.mp4"),
            "-loop", "1", "-framerate", "30", "-t", "30", "-i", Path.Combine(models, "test-faces.jpg"),
            "-vf", "scale=2400:1350,crop=1920:1080:'mod(t*40,480)':100,format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast");
        var options = Options(models);
        using var faces = new YuNetFaceDetector(options, NullLogger<YuNetFaceDetector>.Instance);
        using var plates = new LpdYuNetPlateDetector(options, NullLogger<LpdYuNetPlateDetector>.Instance);
        var processing = Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions());
        var redactor = new MediaRedactor([faces, plates], options, processing, NullLogger<MediaRedactor>.Instance);
        var transcoder = new FfmpegTranscoder(processing, NullLogger<FfmpegTranscoder>.Instance);

        // Warm up the models so neither run pays for loading them.
        faces.Detect(new RgbFrame(64, 64), 0.5f);
        plates.Detect(new RgbFrame(64, 64), 0.5f);

        // One job.
        var timer = Stopwatch.StartNew();
        var probe = await transcoder.ProbeAsync(input, Ct);
        var probeTime = timer.Elapsed;
        var single = await redactor.RedactVideoAsync(input, probe, [], Path.Combine(_folder, "single.mp4"), Ct);
        await transcoder.CreateVideoThumbnailAsync(Path.Combine(_folder, "single.mp4"), Path.Combine(_folder, "single.jpg"), Ct);
        var singleTime = timer.Elapsed;
        Assert.Equal(seconds * 30, single.Frames);

        // Segments, one after another, each timed as its own job.
        var plan = VideoSegmentPlan.For((double)probe.DurationSeconds!.Value, MediaRedactor.OutputFrameRate(probe.FrameRate), probe.StartSeconds ?? 0, 5, 1.0);
        var segmentTimes = new List<TimeSpan>();
        var parts = new List<string>();
        for (var i = 0; i < plan.SegmentCount; i++)
        {
            var part = Path.Combine(_folder, $"part{i:D3}.mp4");
            var segmentTimer = Stopwatch.StartNew();
            await redactor.RedactVideoSegmentAsync(input, plan, i, [], part, Ct);
            segmentTimes.Add(segmentTimer.Elapsed);
            parts.Add(part);
        }

        var joinTimer = Stopwatch.StartNew();
        var joined = Path.Combine(_folder, "joined.mp4");
        await transcoder.ConcatenateVideoAsync(parts, null, joined, Ct);
        await transcoder.CreateVideoThumbnailAsync(joined, Path.Combine(_folder, "joined.jpg"), Ct);
        var joinTime = joinTimer.Elapsed;
        Assert.Equal(seconds * 30, await CountFrames(joined));

        var parallel = probeTime + segmentTimes.Max() + joinTime;
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"30s 1080p30 clip, end to end on {Environment.ProcessorCount} CPUs: one job {singleTime.TotalSeconds:0.0}s " +
            $"({single.DetectionTime.TotalSeconds:0.0}s detecting, {single.FramesAnalysed} frames analysed at <= 640 px); " +
            $"{plan.SegmentCount} segments of 5s took {string.Join(", ", segmentTimes.Select(t => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)))}s " +
            $"(sum {segmentTimes.Sum(t => t.TotalSeconds):0.0}s), join + thumbnail {joinTime.TotalSeconds:0.00}s; " +
            $"estimated with {plan.SegmentCount} parallel replicas {parallel.TotalSeconds:0.0}s " +
            $"({singleTime.TotalSeconds / parallel.TotalSeconds:0.0}x faster).");
        await Report(report);
    }
}

/// <summary>Runs the real-model tests on their own, after the rest, so the timings aren't skewed by other tests.</summary>
[CollectionDefinition(nameof(RealModelTests), DisableParallelization = true)]
public sealed class RealModelTestsCollection;
