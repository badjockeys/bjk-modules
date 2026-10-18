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
    public async Task YuNet_finds_the_faces_in_a_group_photo()
    {
        var models = Models();
        using var detector = new YuNetFaceDetector(Options(models), NullLogger<YuNetFaceDetector>.Instance);
        var frame = Assert.Single(await Frames(Path.Combine(models, "test-faces.jpg")));

        Assert.True(detector.IsAvailable, detector.UnavailableReason);
        var faces = detector.Detect(frame, 0.5f);

        Assert.True(faces.Count(f => f.Confidence >= 0.8f) >= 3, $"Found {faces.Count} faces.");
        Assert.All(faces, f => Assert.InRange(f.Width, 10, 600));
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

    [Fact]
    public async Task A_moving_clip_is_blurred_with_the_real_models_and_timed()
    {
        var models = Models();
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("BJK_REDACTION_PERF_SECONDS"), out var s) ? s : 20;

        // The group photo panning slowly across a 1080p, 30 fps frame: faces move, so tracking is exercised.
        var input = Make(
            Path.Combine(_folder, "clip.mp4"),
            "-loop", "1", "-framerate", "30", "-t", seconds.ToString(CultureInfo.InvariantCulture), "-i", Path.Combine(models, "test-faces.jpg"),
            "-vf", "scale=2400:1350,crop=1920:1080:'mod(t*40,480)':100,format=yuv420p",
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

        Assert.True(result.FacesBlurred >= 3);
        Assert.Equal(seconds * 30, result.Frames);

        var perMinute = timer.Elapsed.TotalSeconds / seconds * 60;
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $"Redaction timing: {seconds}s 1080p30 clip -> 720p, {result.Frames} frames, {result.FramesAnalysed} analysed, " +
            $"{timer.Elapsed.TotalSeconds:0.0}s total ({perMinute:0.0}s per minute of video, ~{perMinute * 5:0}s for 5 minutes), " +
            $"{Environment.ProcessorCount} CPUs, {result.FacesBlurred} faces, {result.PlatesBlurred} plates tracked.");
        TestContext.Current.SendDiagnosticMessage(report);
        Console.WriteLine(report);
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            await File.AppendAllTextAsync(summary, report + Environment.NewLine, Ct);
        }
    }
}
