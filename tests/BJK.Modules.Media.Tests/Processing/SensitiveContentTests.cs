using System.Text;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Processing.Safety;
using BJK.Modules.Media.Infrastructure.Processing.Sensitive;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.Processing.FfmpegTestSupport;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>Automatic graphic content detection: scoring inside the blurring pass, the classifiers and their failure modes.</summary>
public sealed class SensitiveContentTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("bjk-sensitive-tests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static MediaRedactor Redactor(
        ISensitiveContentClassifier? classifier,
        SensitiveDetectionOptions? detection = null,
        int detectionMaxSize = 0,
        IKnownImageList? known = null) => new(
        [FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate)],
        Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions { DetectionMaxSize = detectionMaxSize }),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<MediaRedactor>.Instance,
        classifier,
        Microsoft.Extensions.Options.Options.Create(detection ?? new SensitiveDetectionOptions()),
        known,
        Microsoft.Extensions.Options.Options.Create(new KnownImageOptions()));

    private string Video(string colour = "gray")
    {
        return Make(Path.Combine(_folder, "input.mp4"), "-f", "lavfi", "-i", $"color=c={colour}:s=1920x1080:r=30:d=2,format=yuv420p", "-c:v", "libx264", "-preset", "ultrafast");
    }

    // Scoring inside the blurring pass (real ffmpeg)

    [Fact]
    public async Task Video_frames_are_scored_on_the_scaled_down_copy_every_nth_analysed_frame()
    {
        RequireFfmpeg();
        var classifier = new FakeClassifier(_ => 0.2f);
        var detection = new SensitiveDetectionOptions { SampleEvery = 3 };

        var result = await Redactor(classifier, detection, detectionMaxSize: 640)
            .RedactVideoAsync(Video(), new ProbeResult(true, 2m, 1920, 1080, 30), [], Path.Combine(_folder, "out.mp4"), Ct);

        // 60 frames, every 5th analysed (12), every 3rd of those scored: frames 0, 15, 30, 45.
        Assert.Equal(12, result.FramesAnalysed);
        Assert.Equal(4, result.FramesClassified);
        Assert.Equal(4, classifier.Calls);
        Assert.All(classifier.Sizes, size => Assert.Equal((640, 360), size));
        Assert.Equal(0.2, result.SensitiveScore!.Value, 3);
        Assert.False(result.NeedsReview, string.Join(" ", result.ReviewReasons));
    }

    [Fact]
    public async Task The_highest_score_of_any_frame_is_reported()
    {
        RequireFfmpeg();
        var calls = 0;
        var classifier = new FakeClassifier(_ => ++calls == 3 ? 0.91f : 0.1f);

        var result = await Redactor(classifier, new SensitiveDetectionOptions { SampleEvery = 1 })
            .RedactVideoAsync(Video(), new ProbeResult(true, 2m, 1920, 1080, 30), [], Path.Combine(_folder, "out.mp4"), Ct);

        Assert.Equal(0.91, result.SensitiveScore!.Value, 3);
    }

    [Fact]
    public async Task A_photo_is_scored_once()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "color=c=red:s=800x600", "-frames:v", "1");
        var classifier = new FakeClassifier(f => DevelopmentSensitiveContentClassifier.RedShare(f));

        var result = await Redactor(classifier).RedactImageAsync(input, [], Path.Combine(_folder, "redacted.ppm"), Ct);

        Assert.Equal(1, result.FramesClassified);
        Assert.Equal(1.0, result.SensitiveScore!.Value, 3);
    }

    // Nudity and known images

    [Fact]
    public async Task A_high_nudity_score_sends_the_media_to_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var low = new FakeClassifier(_ => 0f, nudity: 0.05f);
        var high = new FakeClassifier(_ => 0f, nudity: 0.8f);

        var clean = await Redactor(low).RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);
        var held = await Redactor(high).RedactImageAsync(input, [], Path.Combine(_folder, "b.ppm"), Ct);

        Assert.False(clean.NeedsReview, string.Join(" ", clean.ReviewReasons));
        Assert.Equal(0.05, clean.NudityScore!.Value, 3);
        Assert.Contains(held.ReviewReasons, r => r.StartsWith("Possible nudity (score 0.80)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_photo_matching_a_known_image_is_reported_and_its_hash_recorded()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var hit = new KnownImageHit(Guid.NewGuid(), KnownHashKind.Pdq, new string('a', 64), "Test", null, 3);
        var known = Substitute.For<IKnownImageList>();
        known.IsLoaded.Returns(true);
        known.MatchPdq(Arg.Any<PdqHash>()).Returns(hit);

        var result = await Redactor(new FakeClassifier(_ => 0f), known: known).RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);

        Assert.Equal(hit.ToReason(), result.ReviewReasons[0]);
        Assert.Single(result.PerceptualHashes!);
        Assert.Equal(64, result.PerceptualHashes![0].Length);
    }

    [Fact]
    public async Task Until_the_known_image_list_has_loaded_media_goes_to_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var known = Substitute.For<IKnownImageList>();
        known.IsLoaded.Returns(false);

        var result = await Redactor(new FakeClassifier(_ => 0f), known: known).RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);

        Assert.Contains("The known-image list hasn't loaded yet.", result.ReviewReasons);
    }

    [Fact]
    public async Task A_failing_classifier_sends_the_media_to_review_but_the_blurred_file_is_still_made()
    {
        RequireFfmpeg();
        var output = Path.Combine(_folder, "out.mp4");
        var classifier = new FakeClassifier(_ => throw new InvalidOperationException("model crashed"));

        var result = await Redactor(classifier).RedactVideoAsync(Video(), new ProbeResult(true, 2m, 1920, 1080, 30), [], output, Ct);

        Assert.True(result.NeedsReview);
        Assert.Contains(result.ReviewReasons, r => r.Contains("graphic content and nudity check failed"));
        Assert.Equal(1, classifier.Calls);
        Assert.Null(result.SensitiveScore);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task With_detection_on_a_missing_or_unavailable_classifier_means_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var unconfigured = new ClipContentClassifier(
            Microsoft.Extensions.Options.Options.Create(new SensitiveDetectionOptions()),
            Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
            NullLogger<ClipContentClassifier>.Instance);

        var missing = await Redactor(null).RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);
        var unavailable = await Redactor(unconfigured).RedactImageAsync(input, [], Path.Combine(_folder, "b.ppm"), Ct);

        Assert.Contains(missing.ReviewReasons, r => r.Contains("No graphic content classifier"));
        Assert.Contains(unavailable.ReviewReasons, r => r.Contains("Media:SensitiveDetection:ModelPath"));
    }

    [Fact]
    public async Task Without_the_detection_settings_nothing_is_scored()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var classifier = new FakeClassifier(_ => 1f);

        var redactor = new MediaRedactor(
            [FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate)],
            Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
            Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
            NullLogger<MediaRedactor>.Instance,
            classifier,
            detectionOptions: null,
            knownImages: null,
            Microsoft.Extensions.Options.Options.Create(new KnownImageOptions()));

        var result = await redactor.RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);

        Assert.Equal(0, classifier.Calls);
        Assert.Null(result.SensitiveScore);
        Assert.False(result.NeedsReview);
    }

    // The development stand-in

    [Fact]
    public void The_development_classifier_flags_red_pictures_and_only_runs_in_development()
    {
        var development = Substitute.For<IHostEnvironment>();
        development.EnvironmentName.Returns(Environments.Development);
        var production = Substitute.For<IHostEnvironment>();
        production.EnvironmentName.Returns(Environments.Production);

        var classifier = new DevelopmentSensitiveContentClassifier(development);
        Assert.Equal(1f, classifier.Score(Solid(20, 20, 200, 10, 10)));
        Assert.Equal(0f, classifier.Score(Solid(20, 20, 120, 120, 120)));

        var refused = new DevelopmentSensitiveContentClassifier(production);
        Assert.False(refused.IsAvailable);
        Assert.Throws<InvalidOperationException>(() => refused.Score(Solid(20, 20, 200, 10, 10)));
    }

    private static RgbFrame Solid(int width, int height, byte r, byte g, byte b)
    {
        var frame = new RgbFrame(width, height);
        for (var i = 0; i < frame.Pixels.Length; i += 3)
        {
            frame.Pixels[i] = r;
            frame.Pixels[i + 1] = g;
            frame.Pixels[i + 2] = b;
        }

        return frame;
    }
}

/// <summary>A classifier for tests: scores with the given function and remembers the frame sizes it saw.</summary>
internal sealed class FakeClassifier(Func<RgbFrame, float> score, bool available = true, float? nudity = null) : ISensitiveContentClassifier
{
    public ContentScores Classify(RgbFrame frame) => new(Score(frame), nudity);

    private readonly object _lock = new();

    public int Calls { get; private set; }

    public List<(int Width, int Height)> Sizes { get; } = [];

    public bool IsAvailable => available;

    public string? UnavailableReason => available ? null : "The graphic content model is not configured.";

    public float Score(RgbFrame frame)
    {
        lock (_lock)
        {
            Calls++;
            Sizes.Add((frame.Width, frame.Height));
        }

        return score(frame);
    }
}
