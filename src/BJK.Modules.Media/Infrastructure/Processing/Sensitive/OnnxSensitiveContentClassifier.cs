using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace BJK.Modules.Media.Infrastructure.Processing.Sensitive;

/// <summary>
/// Runs an image classification model (ONNX, NCHW float input of
/// <see cref="SensitiveDetectionOptions.InputWidth"/> x <see cref="SensitiveDetectionOptions.InputHeight"/>, RGB scaled
/// to 0 to 1 then normalised with <see cref="SensitiveDetectionOptions.Mean"/> and <see cref="SensitiveDetectionOptions.Std"/>;
/// first output [1, classes]) on the CPU with ONNX Runtime, as the face and plate detectors do. Fits most classifiers
/// exported from PyTorch or timm (MobileNet, EfficientNet, ViT). The model is loaded on first use; if it isn't
/// configured, is missing or won't load, the classifier reports itself unavailable and media goes to review.
/// </summary>
internal sealed class OnnxSensitiveContentClassifier : ISensitiveContentClassifier, IDisposable
{
    private readonly SensitiveDetectionOptions _options;
    private readonly ILogger<OnnxSensitiveContentClassifier> _logger;
    private readonly Lazy<InferenceSession?> _session;
    private readonly float[] _mean;
    private readonly float[] _std;
    private readonly int[] _classes;
    private string? _unavailableReason;

    public OnnxSensitiveContentClassifier(
        IOptions<SensitiveDetectionOptions> options,
        IOptions<MediaRedactionOptions> redactionOptions,
        ILogger<OnnxSensitiveContentClassifier> logger)
    {
        _options = options.Value;
        _logger = logger;
        _mean = _options.MeanValues;
        _std = _options.StdValues;
        _classes = _options.SensitiveClassIndices;
        var threads = redactionOptions.Value.EffectiveInferenceThreads;
        _session = new Lazy<InferenceSession?>(() => Load(threads), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _session.Value is not null;

    public string? UnavailableReason => IsAvailable ? null : _unavailableReason;

    public float Score(RgbFrame frame)
    {
        var session = _session.Value ?? throw new InvalidOperationException(_unavailableReason ?? "The graphic content model isn't available.");
        var (width, height) = (_options.InputWidth, _options.InputHeight);
        var input = ToInput(frame, width, height, _mean, _std);

        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [session.InputNames[0]], [tensor], [session.OutputNames[0]]);
        var values = outputs.First().GetTensorDataAsSpan<float>().ToArray();
        return ScoreOf(values, _options.Output, _classes);
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value?.Dispose();
        }
    }

    /// <summary>The model input: the frame resized to width x height, as planar RGB floats normalised per channel.</summary>
    internal static float[] ToInput(RgbFrame frame, int width, int height, float[] mean, float[] std)
    {
        var resized = frame.Width == width && frame.Height == height ? frame : FrameOps.Resize(frame, width, height);
        var plane = width * height;
        var data = new float[plane * 3];
        var pixels = resized.Pixels;
        for (var i = 0; i < plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                data[(c * plane) + i] = ((pixels[(i * 3) + c] / 255f) - mean[c]) / std[c];
            }
        }

        return data;
    }

    /// <summary>Turns the model's output into a 0 to 1 score for the sensitive classes.</summary>
    internal static float ScoreOf(float[] values, SensitiveModelOutput output, int[] classes)
    {
        if (classes.Any(c => c >= values.Length))
        {
            throw new InvalidOperationException(
                $"The graphic content model has {values.Length} output classes; Media:SensitiveDetection:SensitiveClasses names a class beyond that.");
        }

        float score;
        switch (output)
        {
            case SensitiveModelOutput.Sigmoid:
                score = classes.Max(c => 1f / (1f + MathF.Exp(-values[c])));
                break;
            case SensitiveModelOutput.Probabilities:
                score = classes.Sum(c => values[c]);
                break;
            default:
                var max = values.Max();
                var exp = values.Select(v => MathF.Exp(v - max)).ToArray();
                var total = exp.Sum();
                score = classes.Sum(c => exp[c]) / total;
                break;
        }

        if (float.IsNaN(score))
        {
            throw new InvalidOperationException("The graphic content model returned a score that isn't a number.");
        }

        return Math.Clamp(score, 0f, 1f);
    }

    private InferenceSession? Load(int threads)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            _unavailableReason = "The graphic content model is not configured (Media:SensitiveDetection:ModelPath).";
            return null;
        }

        if (!File.Exists(_options.ModelPath))
        {
            _unavailableReason = "The graphic content model file is missing (Media:SensitiveDetection:ModelPath).";
            _logger.LogError("The graphic content model {Path} doesn't exist; media will go to review.", _options.ModelPath);
            return null;
        }

        try
        {
            using var sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = threads,
            };
            sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            var session = new InferenceSession(_options.ModelPath, sessionOptions);
            _logger.LogInformation("Loaded the graphic content model with {Threads} threads.", threads);
            return session;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException
            or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
        {
            _unavailableReason = "The graphic content model couldn't be loaded.";
            _logger.LogError(ex, "Couldn't load the graphic content model {Path}; media will go to review.", _options.ModelPath);
            return null;
        }
    }
}
