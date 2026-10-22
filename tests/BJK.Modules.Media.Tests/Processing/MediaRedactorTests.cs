using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BJK.Modules.Media.Tests.Processing.FfmpegTestSupport;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>Blurring with the real ffmpeg and stand-in detectors. Skipped where ffmpeg isn't installed; CI installs it.</summary>
public sealed class MediaRedactorTests : IDisposable
{
    // A "face" in 720p output coordinates; with 25% padding it covers 450..750 x 162..388.
    private static readonly DetectionBox Face = new(RedactionTarget.Face, 500, 200, 200, 150, 0.95f);

    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-redaction-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MediaRedactor Redactor(MediaRedactionOptions? options = null, params IRedactionDetector[] detectors) => new(
        detectors.Length > 0 ? detectors : [FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate)],
        // The stand-in detectors answer in full-frame coordinates, so detect at full size unless a test says otherwise.
        Microsoft.Extensions.Options.Options.Create(options ?? new MediaRedactionOptions { DetectionMaxSize = 0 }),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<MediaRedactor>.Instance);

    private string CheckerVideo(string name, int seconds = 2, bool audio = true)
    {
        var arguments = new List<string>
        {
            "-f", "lavfi", "-i", $"color=c=gray:s=1920x1080:r=60:d={seconds},format=yuv420p,{Checkerboard}",
        };
        if (audio)
        {
            arguments.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-c:a", "aac"]);
        }

        arguments.AddRange(["-metadata", "location=+53.7457-001.7181/", "-c:v", "libx264", "-preset", "ultrafast", "-shortest"]);
        return Make(Path.Combine(_folder, name), [.. arguments]);
    }

    [Fact]
    public async Task A_video_is_blurred_where_detected_scaled_to_720p_30fps_and_loses_sound_and_gps()
    {
        RequireFfmpeg();
        var input = CheckerVideo("input.mp4");
        var output = Path.Combine(_folder, "720p.mp4");
        var face = new FakeDetector(RedactionTarget.Face, _ => [Face]);
        var plates = FakeDetector.Nothing(RedactionTarget.NumberPlate);

        var result = await Redactor(null, face, plates).RedactVideoAsync(input, new ProbeResult(true, 2m, 1920, 1080, 60), [], output, Ct);

        Assert.False(result.NeedsReview, string.Join(" ", result.ReviewReasons));
        Assert.Equal(1, result.FacesBlurred);
        Assert.Equal(60, result.Frames);
        Assert.Equal(12, result.FramesAnalysed);
        Assert.Equal(12, face.Calls);

        var info = await Ffprobe(output);
        Assert.Contains("codec_type=video", info);
        Assert.DoesNotContain("codec_type=audio", info);
        Assert.DoesNotContain("53.7457", info);
        Assert.Contains("width=1280", info);
        Assert.Contains("height=720", info);
        Assert.Contains("avg_frame_rate=30/1", info);

        var frames = await Frames(output);
        Assert.Equal(60, frames.Count);
        foreach (var frame in new[] { frames[0], frames[7], frames[^1] })
        {
            Assert.InRange(Detail(frame, 520, 220, 680, 330), 0, 30);
            Assert.InRange(Detail(frame, 900, 400, 1200, 700), 80, 255);
        }
    }

    [Fact]
    public async Task Video_plates_are_searched_in_a_640_pixel_copy_and_faces_in_a_1440_one_and_boxes_are_mapped_back()
    {
        RequireFfmpeg();
        var input = CheckerVideo("input.mp4", seconds: 1, audio: false);
        var output = Path.Combine(_folder, "720p.mp4");
        var sizes = new List<(int, int)>();
        var plateSizes = new List<(int, int)>();

        // Answers in the coordinates of whatever it's given: the same spot as Face, as fractions of the picture.
        var face = new FakeDetector(RedactionTarget.Face, f =>
        {
            lock (sizes)
            {
                sizes.Add((f.Width, f.Height));
            }

            var (sx, sy) = (f.Width / 1280f, f.Height / 720f);
            return [new DetectionBox(RedactionTarget.Face, Face.X * sx, Face.Y * sy, Face.Width * sx, Face.Height * sy, 0.95f)];
        });

        var plates = new FakeDetector(RedactionTarget.NumberPlate, f =>
        {
            lock (plateSizes)
            {
                plateSizes.Add((f.Width, f.Height));
            }

            return [];
        });

        await Redactor(new MediaRedactionOptions(), face, plates)
            .RedactVideoAsync(input, new ProbeResult(true, 1m, 1920, 1080, 60), [], output, Ct);

        Assert.NotEmpty(sizes);
        Assert.All(sizes, s => Assert.Equal((1440, 810), s));
        Assert.All(plateSizes, s => Assert.Equal((640, 360), s));
        var frames = await Frames(output);
        Assert.InRange(Detail(frames[10], 520, 220, 680, 330), 0, 30);
        Assert.InRange(Detail(frames[10], 900, 400, 1200, 700), 80, 255);
    }

    [Theory]
    [InlineData(1280, 720, 640, 0.5)]
    [InlineData(640, 360, 640, 1.0)]
    [InlineData(720, 1280, 640, 0.5)]
    [InlineData(1920, 1080, 0, 1.0)]
    public void Detection_scale_fits_the_longest_side(int width, int height, int max, double expected)
    {
        Assert.Equal(expected, (double)MediaRedactor.DetectionScale(width, height, max), 3);
    }

    [Fact]
    public async Task Sound_is_kept_only_when_audio_removal_is_switched_off()
    {
        RequireFfmpeg();
        var input = CheckerVideo("input.mp4", seconds: 1);
        var output = Path.Combine(_folder, "720p.mp4");

        await Redactor(new MediaRedactionOptions { RemoveAudio = false, DetectionMaxSize = 0 }).RedactVideoAsync(input, new ProbeResult(true, 1m, 1920, 1080, 60), [], output, Ct);

        Assert.Contains("codec_type=audio", await Ffprobe(output));
    }

    [Fact]
    public async Task A_box_between_detection_passes_is_followed_without_gaps()
    {
        RequireFfmpeg();
        var input = CheckerVideo("input.mp4", seconds: 1, audio: false);
        var output = Path.Combine(_folder, "720p.mp4");

        // Seen only on the first detection pass and the fourth (frames 0 and 15); missed on 5 and 10.
        var call = 0;
        var face = new FakeDetector(RedactionTarget.Face, _ => call++ is 0 or 3 ? new List<DetectionBox> { Face } : new List<DetectionBox>());
        var result = await Redactor(null, face, FakeDetector.Nothing(RedactionTarget.NumberPlate))
            .RedactVideoAsync(input, new ProbeResult(true, 1m, 1920, 1080, 60), [], output, Ct);

        Assert.Equal(1, result.FacesBlurred);
        var frames = await Frames(output);
        foreach (var index in new[] { 0, 3, 8, 12, 15, 19 })
        {
            Assert.InRange(Detail(frames[index], 520, 220, 680, 330), 0, 30);
        }

        // More than one interval after the last detection, the blur ends.
        Assert.InRange(Detail(frames[25], 520, 220, 680, 330), 80, 255);
    }

    [Fact]
    public async Task Manual_regions_apply_only_within_their_time_range()
    {
        RequireFfmpeg();
        var input = CheckerVideo("input.mp4", seconds: 2, audio: false);
        var output = Path.Combine(_folder, "720p.mp4");
        var region = new ManualRedaction(0.5, 0.5, 0.25, 0.25, StartSeconds: 1.0, EndSeconds: 2.0);

        await Redactor().RedactVideoAsync(input, new ProbeResult(true, 2m, 1920, 1080, 60), [region], output, Ct);

        var frames = await Frames(output);
        Assert.InRange(Detail(frames[10], 660, 380, 940, 520), 80, 255);
        Assert.InRange(Detail(frames[45], 660, 380, 940, 520), 0, 30);
    }

    [Fact]
    public async Task A_photo_is_blurred_where_detected_and_written_upright_at_most_2048_pixels()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", $"color=c=gray:s=4000x3000,format=yuv420p,{Checkerboard}", "-frames:v", "1");
        var output = Path.Combine(_folder, "redacted.ppm");
        var plate = new FakeDetector(RedactionTarget.NumberPlate, f => [new DetectionBox(RedactionTarget.NumberPlate, 1000, 1000, 400, 100, 0.9f)]);

        var result = await Redactor(null, FakeDetector.Nothing(RedactionTarget.Face), plate).RedactImageAsync(input, [], output, Ct);

        Assert.False(result.NeedsReview);
        Assert.Equal(1, result.PlatesBlurred);
        var frame = Assert.Single(await Frames(output));
        Assert.Equal((2048, 1536), (frame.Width, frame.Height));
        Assert.InRange(Detail(frame, 1020, 1010, 1380, 1090), 0, 30);
        Assert.InRange(Detail(frame, 100, 100, 500, 500), 80, 255);
    }

    [Fact]
    public async Task Missing_models_mean_review_but_the_blurred_file_is_still_made()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var output = Path.Combine(_folder, "redacted.ppm");
        var options = Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions());
        using var faces = new YuNetFaceDetector(options, NullLogger<YuNetFaceDetector>.Instance);
        using var plates = new LpdYuNetPlateDetector(options, NullLogger<LpdYuNetPlateDetector>.Instance);

        var result = await Redactor(null, faces, plates).RedactImageAsync(input, [], output, Ct);

        Assert.True(result.NeedsReview);
        Assert.Contains(result.ReviewReasons, r => r.Contains("FaceModelPath"));
        Assert.Contains(result.ReviewReasons, r => r.Contains("PlateModelPath"));
        Assert.True(new FileInfo(output).Length > 800 * 600 * 3);
    }

    [Fact]
    public async Task A_failing_detector_or_an_uncertain_detection_means_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var broken = new FakeDetector(RedactionTarget.Face, _ => throw new InvalidOperationException("model crashed"));
        var unsure = new FakeDetector(RedactionTarget.NumberPlate, _ => [new DetectionBox(RedactionTarget.NumberPlate, 10, 10, 80, 20, 0.6f)]);

        var result = await Redactor(null, broken, unsure).RedactImageAsync(input, [], Path.Combine(_folder, "redacted.ppm"), Ct);

        Assert.True(result.NeedsReview);
        Assert.Contains(result.ReviewReasons, r => r.Contains("Face detector failed"));
        Assert.Contains(result.ReviewReasons, r => r.StartsWith("1 number plate had low confidence", StringComparison.Ordinal));
        Assert.Equal(1, result.PlatesBlurred);
    }

    [Fact]
    public async Task A_head_from_the_person_detector_is_blurred_without_sending_the_photo_to_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", $"color=c=gray:s=800x600,format=yuv420p,{Checkerboard}", "-frames:v", "1");
        var output = Path.Combine(_folder, "redacted.ppm");
        var heads = new FakeDetector(RedactionTarget.Head, _ => [new DetectionBox(RedactionTarget.Head, 300, 100, 120, 60, 0.5f)]);

        var result = await Redactor(null, FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate), heads)
            .RedactImageAsync(input, [], output, Ct);

        Assert.False(result.NeedsReview, string.Join(" ", result.ReviewReasons));
        var frame = Assert.Single(await Frames(output));
        Assert.InRange(Detail(frame, 310, 110, 410, 150), 0, 30);
        Assert.InRange(Detail(frame, 600, 400, 780, 580), 80, 255);
    }

    [Fact]
    public async Task An_unavailable_person_detector_means_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var heads = new FakeDetector(RedactionTarget.Head, _ => [], available: false);

        var result = await Redactor(null, FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate), heads)
            .RedactImageAsync(input, [], Path.Combine(_folder, "redacted.ppm"), Ct);

        Assert.True(result.NeedsReview);
        Assert.Contains(result.ReviewReasons, r => r.Contains("Head"));
    }

    [Fact]
    public async Task Review_everything_sends_confident_results_to_review_too()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");

        var result = await Redactor(new MediaRedactionOptions { ReviewEverything = true, DetectionMaxSize = 0 })
            .RedactImageAsync(input, [], Path.Combine(_folder, "redacted.ppm"), Ct);

        Assert.True(result.NeedsReview);
    }
}
