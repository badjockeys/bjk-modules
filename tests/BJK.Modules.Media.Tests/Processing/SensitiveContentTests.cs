using System.Text;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
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

    private static MediaRedactor Redactor(ISensitiveContentClassifier? classifier, SensitiveDetectionOptions? detection = null, int detectionMaxSize = 0) => new(
        [FakeDetector.Nothing(RedactionTarget.Face), FakeDetector.Nothing(RedactionTarget.NumberPlate)],
        Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions { DetectionMaxSize = detectionMaxSize }),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions()),
        NullLogger<MediaRedactor>.Instance,
        classifier,
        Microsoft.Extensions.Options.Options.Create(detection ?? new SensitiveDetectionOptions { Enabled = true }));

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
        var detection = new SensitiveDetectionOptions { Enabled = true, SampleEvery = 3 };

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

        var result = await Redactor(classifier, new SensitiveDetectionOptions { Enabled = true, SampleEvery = 1 })
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

    [Fact]
    public async Task A_failing_classifier_sends_the_media_to_review_but_the_blurred_file_is_still_made()
    {
        RequireFfmpeg();
        var output = Path.Combine(_folder, "out.mp4");
        var classifier = new FakeClassifier(_ => throw new InvalidOperationException("model crashed"));

        var result = await Redactor(classifier).RedactVideoAsync(Video(), new ProbeResult(true, 2m, 1920, 1080, 30), [], output, Ct);

        Assert.True(result.NeedsReview);
        Assert.Contains(result.ReviewReasons, r => r.Contains("graphic content check failed"));
        Assert.Equal(1, classifier.Calls);
        Assert.Null(result.SensitiveScore);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task With_detection_on_a_missing_or_unavailable_classifier_means_review()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var unconfigured = new OnnxSensitiveContentClassifier(
            Microsoft.Extensions.Options.Options.Create(new SensitiveDetectionOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
            NullLogger<OnnxSensitiveContentClassifier>.Instance);

        var missing = await Redactor(null).RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);
        var unavailable = await Redactor(unconfigured).RedactImageAsync(input, [], Path.Combine(_folder, "b.ppm"), Ct);

        Assert.Contains(missing.ReviewReasons, r => r.Contains("No graphic content classifier"));
        Assert.Contains(unavailable.ReviewReasons, r => r.Contains("Media:SensitiveDetection:ModelPath"));
    }

    [Fact]
    public async Task With_detection_off_nothing_is_scored()
    {
        RequireFfmpeg();
        var input = Make(Path.Combine(_folder, "photo.png"), "-f", "lavfi", "-i", "testsrc=size=800x600", "-frames:v", "1");
        var classifier = new FakeClassifier(_ => 1f);

        var result = await Redactor(classifier, new SensitiveDetectionOptions { Enabled = false })
            .RedactImageAsync(input, [], Path.Combine(_folder, "a.ppm"), Ct);

        Assert.Equal(0, classifier.Calls);
        Assert.Null(result.SensitiveScore);
        Assert.False(result.NeedsReview);
    }

    // The ONNX classifier

    [Fact]
    public void Model_outputs_are_turned_into_a_score_for_the_sensitive_classes()
    {
        Assert.Equal(0.5f, OnnxSensitiveContentClassifier.ScoreOf([0f, 0f], SensitiveModelOutput.Softmax, [1]), 3);
        Assert.Equal(0.881f, OnnxSensitiveContentClassifier.ScoreOf([0f, 2f], SensitiveModelOutput.Softmax, [1]), 3);
        Assert.Equal(0.731f, OnnxSensitiveContentClassifier.ScoreOf([-3f, 1f, -1f], SensitiveModelOutput.Sigmoid, [1, 2]), 3);
        Assert.Equal(0.7f, OnnxSensitiveContentClassifier.ScoreOf([0.3f, 0.2f, 0.5f], SensitiveModelOutput.Probabilities, [1, 2]), 3);
        Assert.Throws<InvalidOperationException>(() => OnnxSensitiveContentClassifier.ScoreOf([0f, 1f], SensitiveModelOutput.Softmax, [2]));
    }

    [Fact]
    public void Input_is_resized_planar_rgb_normalised_per_channel()
    {
        var frame = Solid(10, 10, 255, 0, 51);

        var input = OnnxSensitiveContentClassifier.ToInput(frame, 4, 4, [0.5f, 0f, 0f], [0.5f, 1f, 0.1f]);

        Assert.Equal(3 * 16, input.Length);
        Assert.All(input[..16], v => Assert.Equal(1f, v, 3));
        Assert.All(input[16..32], v => Assert.Equal(0f, v, 3));
        Assert.All(input[32..], v => Assert.Equal(2f, v, 3));
    }

    [Fact]
    public void A_real_onnx_model_is_loaded_and_run()
    {
        // A tiny hand-built model: the mean of each colour channel, as "probabilities" of three classes.
        var path = Path.Combine(_folder, "mean-colour.onnx");
        File.WriteAllBytes(path, TinyOnnxModel.ChannelMeans(224, 224));
        using var classifier = new OnnxSensitiveContentClassifier(
            Microsoft.Extensions.Options.Options.Create(new SensitiveDetectionOptions
            {
                Enabled = true,
                ModelPath = path,
                Mean = "0,0,0",
                Std = "1,1,1",
                Output = SensitiveModelOutput.Probabilities,
                SensitiveClasses = "0",
            }),
            Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions { InferenceThreads = 1 }),
            NullLogger<OnnxSensitiveContentClassifier>.Instance);

        Assert.True(classifier.IsAvailable, classifier.UnavailableReason);
        Assert.Equal(1f, classifier.Score(Solid(640, 360, 255, 0, 0)), 2);
        Assert.Equal(0f, classifier.Score(Solid(640, 360, 0, 0, 255)), 2);
    }

    [Fact]
    public void A_missing_model_file_makes_the_classifier_unavailable()
    {
        var classifier = new OnnxSensitiveContentClassifier(
            Microsoft.Extensions.Options.Options.Create(new SensitiveDetectionOptions { Enabled = true, ModelPath = Path.Combine(_folder, "nope.onnx") }),
            Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
            NullLogger<OnnxSensitiveContentClassifier>.Instance);

        Assert.False(classifier.IsAvailable);
        Assert.Contains("missing", classifier.UnavailableReason);
        Assert.Throws<InvalidOperationException>(() => classifier.Score(Solid(10, 10, 0, 0, 0)));
    }

    [Fact]
    public void Settings_with_bad_lists_are_refused()
    {
        Assert.True(new SensitiveDetectionOptions().IsValid());
        Assert.False(new SensitiveDetectionOptions { Mean = "0.5,0.5" }.IsValid());
        Assert.False(new SensitiveDetectionOptions { Std = "1,0,1" }.IsValid());
        Assert.False(new SensitiveDetectionOptions { SensitiveClasses = "" }.IsValid());
        Assert.False(new SensitiveDetectionOptions { SensitiveClasses = "one" }.IsValid());
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
internal sealed class FakeClassifier(Func<RgbFrame, float> score, bool available = true) : ISensitiveContentClassifier
{
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

/// <summary>
/// Writes a minimal ONNX model by hand (protobuf), so the ONNX classifier can be tested without downloading a model:
/// input "x" float [1, 3, H, W], one ReduceMean over the picture (opset 13), output "m" float [1, 3].
/// </summary>
internal static class TinyOnnxModel
{
    public static byte[] ChannelMeans(int width, int height)
    {
        var axes = Bytes(Field(1, "axes"), Varint(8, 2), Varint(8, 3), Varint(20, 7));
        var keepDims = Bytes(Field(1, "keepdims"), Varint(3, 0), Varint(20, 2));
        var node = Bytes(Field(1, "x"), Field(2, "m"), Field(3, "mean"), Field(4, "ReduceMean"), Field(5, axes), Field(5, keepDims));
        var graph = Bytes(
            Field(1, node),
            Field(2, "g"),
            Field(11, ValueInfo("x", 1, 3, height, width)),
            Field(12, ValueInfo("m", 1, 3)));
        return Bytes(Varint(1, 8), Field(2, "bjk-test"), Field(7, graph), Field(8, Bytes(Field(1, string.Empty), Varint(2, 13))));
    }

    private static byte[] ValueInfo(string name, params long[] dims)
    {
        var shape = Bytes(dims.Select(d => Field(1, Varint(1, d))).ToArray());
        var tensor = Bytes(Varint(1, 1), Field(2, shape));
        return Bytes(Field(1, name), Field(2, Field(1, tensor)));
    }

    private static byte[] Varint(int field, long value) => Bytes(Raw(((ulong)field << 3) | 0), Raw((ulong)value));

    private static byte[] Field(int field, string value) => Field(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Field(int field, byte[] value) => Bytes(Raw(((ulong)field << 3) | 2), Raw((ulong)value.Length), value);

    private static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);

        return [.. bytes];
    }

    private static byte[] Bytes(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
