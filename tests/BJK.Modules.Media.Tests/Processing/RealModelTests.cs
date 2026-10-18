using System.Diagnostics;
using System.Globalization;
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
/// The timing test's clip length is <c>BJK_REDACTION_PERF_SECONDS</c> (default 20).
/// </summary>
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

    [Theory]
    [InlineData("crowd")]
    [InlineData("scene")]
    public async Task A_moving_clip_is_blurred_with_the_real_models_and_timed(string kind)
    {
        var models = Models();
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("BJK_REDACTION_PERF_SECONDS"), out var s) ? s : 20;
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
        TestContext.Current.SendDiagnosticMessage(report);
        Console.WriteLine(report);
        foreach (var file in new[] { "GITHUB_STEP_SUMMARY", "BJK_REDACTION_TIMING_FILE" }.Select(Environment.GetEnvironmentVariable))
        {
            if (!string.IsNullOrEmpty(file))
            {
                await File.AppendAllTextAsync(file, report + Environment.NewLine, Ct);
            }
        }
    }
}
