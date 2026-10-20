using System.Text.Json;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;

namespace BJK.Modules.Media.Infrastructure.Processing.Sensitive;

/// <summary>
/// Scores graphic content and nudity with one model: OpenAI's CLIP (ViT-B/32, MIT licence) as a zero-shot classifier.
/// The picture's CLIP embedding (from the ONNX vision model at <see cref="SensitiveDetectionOptions.ModelPath"/>,
/// Xenova's export) is compared with the embeddings of short descriptions built into the module
/// (<c>clip-prompts.json</c>): ordinary road scenes ("a dashcam video frame of traffic", "a car crash with damaged
/// vehicles"...), graphic content ("a dead body", "severe wounds and blood"...) and nudity ("a photo of a naked
/// person", "pornography"...). A softmax over all of them gives each group's share: the graphic share is the graphic
/// content score, the nudity share the nudity score. One model run per frame covers both checks.
/// </summary>
/// <remarks>
/// Zero-shot scores are rough, so they only ever add caution: a high graphic score marks media sensitive (shown
/// blurred, and a moderator confirms), a high nudity score sends it to a moderator before it's published. Neither
/// removes anything by itself. Tune <see cref="SensitiveDetectionOptions.Threshold"/> and
/// <see cref="SensitiveDetectionOptions.NudityThreshold"/> with real uploads.
/// </remarks>
internal sealed class ClipContentClassifier : ISensitiveContentClassifier, IDisposable
{
    /// <summary>CLIP's input size and per-channel normalisation.</summary>
    public const int InputSize = 224;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly SensitiveDetectionOptions _options;
    private readonly ILogger<ClipContentClassifier> _logger;
    private readonly Lazy<InferenceSession?> _session;
    private readonly ClipPrompts _prompts = ClipPrompts.Builtin.Value;
    private string? _unavailableReason;

    public ClipContentClassifier(
        IOptions<SensitiveDetectionOptions> options,
        IOptions<MediaRedactionOptions> redactionOptions,
        ILogger<ClipContentClassifier> logger)
    {
        _options = options.Value;
        _logger = logger;
        var threads = redactionOptions.Value.EffectiveInferenceThreads;
        _session = new Lazy<InferenceSession?>(() => Load(threads), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _session.Value is not null;

    public string? UnavailableReason => IsAvailable ? null : _unavailableReason;

    public float Score(RgbFrame frame) => Classify(frame).Graphic;

    public ContentScores Classify(RgbFrame frame)
    {
        var session = _session.Value ?? throw new InvalidOperationException(_unavailableReason ?? "The content model isn't available.");
        var input = ToInput(frame);
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, InputSize, InputSize]);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [session.InputNames[0]], [tensor], [session.OutputNames[0]]);
        var embedding = outputs.First().GetTensorDataAsSpan<float>().ToArray();
        return _prompts.Score(embedding);
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value?.Dispose();
        }
    }

    /// <summary>The model input: the centre square, resized to 224 x 224, as planar RGB normalised the way CLIP was trained.</summary>
    internal static float[] ToInput(RgbFrame frame)
    {
        var side = Math.Min(frame.Width, frame.Height);
        var crop = FrameOps.Resize(frame, InputSize, InputSize, (frame.Width - side) / 2, (frame.Height - side) / 2, side, side);
        const int Plane = InputSize * InputSize;
        var data = new float[Plane * 3];
        var pixels = crop.Pixels;
        for (var i = 0; i < Plane; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                data[(c * Plane) + i] = ((pixels[(i * 3) + c] / 255f) - Mean[c]) / Std[c];
            }
        }

        return data;
    }

    private InferenceSession? Load(int threads)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            _unavailableReason = "The content model is not configured (Media:SensitiveDetection:ModelPath).";
            return null;
        }

        if (!File.Exists(_options.ModelPath))
        {
            _unavailableReason = "The content model file is missing (Media:SensitiveDetection:ModelPath).";
            _logger.LogError("The content model {Path} doesn't exist; media will go to review.", _options.ModelPath);
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
            _logger.LogInformation("Loaded the CLIP content model with {Threads} threads.", threads);
            return session;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException
            or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
        {
            _unavailableReason = "The content model couldn't be loaded.";
            _logger.LogError(ex, "Couldn't load the content model {Path}; media will go to review.", _options.ModelPath);
            return null;
        }
    }
}

/// <summary>The built-in CLIP text embeddings: descriptions in three groups, and CLIP's logit scale.</summary>
internal sealed class ClipPrompts
{
    public static readonly Lazy<ClipPrompts> Builtin = new(Load);

    private ClipPrompts(float logitScale, float[][] embeddings, int[] groups)
    {
        LogitScale = logitScale;
        Embeddings = embeddings;
        Groups = groups;
    }

    /// <summary>0 safe, 1 graphic, 2 nudity, for each embedding.</summary>
    public int[] Groups { get; }

    public float[][] Embeddings { get; }

    public float LogitScale { get; }

    /// <summary>The graphic and nudity shares of the softmax over every description, for an image embedding.</summary>
    public ContentScores Score(float[] imageEmbedding)
    {
        var norm = MathF.Sqrt(imageEmbedding.Sum(v => v * v));
        if (norm <= 0 || float.IsNaN(norm))
        {
            throw new InvalidOperationException("The content model returned an empty embedding.");
        }

        var logits = new float[Embeddings.Length];
        for (var p = 0; p < logits.Length; p++)
        {
            var dot = 0f;
            var text = Embeddings[p];
            for (var i = 0; i < text.Length; i++)
            {
                dot += imageEmbedding[i] * text[i];
            }

            logits[p] = LogitScale * dot / norm;
        }

        var max = logits.Max();
        var exp = logits.Select(l => MathF.Exp(l - max)).ToArray();
        var total = exp.Sum();
        float Share(int group) => exp.Where((_, i) => Groups[i] == group).Sum() / total;
        return new ContentScores(Math.Clamp(Share(1), 0f, 1f), Math.Clamp(Share(2), 0f, 1f));
    }

    private static ClipPrompts Load()
    {
        using var stream = typeof(ClipPrompts).Assembly.GetManifestResourceStream("BJK.Modules.Media.clip-prompts.json")
            ?? throw new InvalidOperationException("The CLIP prompts are missing from the module.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var embeddings = new List<float[]>();
        var groups = new List<int>();
        string[] names = ["safe", "graphic", "nudity"];
        for (var g = 0; g < names.Length; g++)
        {
            foreach (var prompt in root.GetProperty("groups").GetProperty(names[g]).EnumerateArray())
            {
                embeddings.Add(prompt.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray());
                groups.Add(g);
            }
        }

        return new ClipPrompts(root.GetProperty("logitScale").GetSingle(), embeddings.ToArray(), groups.ToArray());
    }
}
