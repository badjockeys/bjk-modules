using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Shared plumbing for detectors that run an ONNX model with ONNX Runtime, on the CPU by default
/// (<see cref="MediaRedactionOptions.ExecutionProvider"/>). The model is loaded on first use; if the path isn't set,
/// the file is missing or it won't load (including a GPU provider the installed ONNX Runtime package doesn't have), the
/// detector reports itself unavailable (and the media goes to review) rather than failing the job.
/// </summary>
internal abstract class OnnxDetector : IRedactionDetector, IDisposable
{
    private readonly Lazy<InferenceSession?> _session;
    private readonly string? _modelPath;
    private readonly ILogger _logger;
    private string? _unavailableReason;

    /// <param name="modelPath">The .onnx file, or null if not configured.</param>
    /// <param name="settingName">Configuration key, named in the reason when it's missing.</param>
    /// <param name="options">Threads and execution provider.</param>
    /// <param name="logger">For load failures.</param>
    protected OnnxDetector(string? modelPath, string settingName, MediaRedactionOptions options, ILogger logger)
    {
        _modelPath = modelPath;
        _logger = logger;
        _session = new Lazy<InferenceSession?>(
            () => Load(settingName, options.EffectiveInferenceThreads, options.ExecutionProvider, options.CudaDeviceId),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public abstract RedactionTarget Target { get; }

    public bool IsAvailable => _session.Value is not null;

    public string? UnavailableReason => IsAvailable ? null : _unavailableReason;

    protected InferenceSession Session =>
        _session.Value ?? throw new InvalidOperationException(_unavailableReason ?? "The model isn't available.");

    public IReadOnlyList<DetectionBox> Detect(RgbFrame frame, float minConfidence) => DetectCore(frame, minConfidence);

    public void Dispose()
    {
        if (_session.IsValueCreated)
        {
            _session.Value?.Dispose();
        }
    }

    protected abstract IReadOnlyList<DetectionBox> DetectCore(RgbFrame frame, float minConfidence);

    /// <summary>The model's fixed input size (width, height), or null if it accepts any size.</summary>
    protected (int Width, int Height)? FixedInputSize()
    {
        var dims = Session.InputMetadata.Values.First().Dimensions;
        return dims.Length == 4 && dims[2] > 0 && dims[3] > 0 ? (dims[3], dims[2]) : null;
    }

    /// <summary>Runs the model on one NCHW float input and returns every output by name.</summary>
    protected Dictionary<string, (float[] Data, long[] Shape)> Run(float[] input, int width, int height)
    {
        var inputName = Session.InputNames[0];
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, height, width]);
        using var options = new RunOptions();
        using var outputs = Session.Run(options, [inputName], [tensor], Session.OutputNames);

        var results = new Dictionary<string, (float[] Data, long[] Shape)>(StringComparer.Ordinal);
        var index = 0;
        foreach (var output in outputs)
        {
            results[Session.OutputNames[index++]] = (
                output.GetTensorDataAsSpan<float>().ToArray(),
                output.GetTensorTypeAndShape().Shape);
        }

        return results;
    }

    private InferenceSession? Load(string settingName, int threads, RedactionExecutionProvider provider, int deviceId)
    {
        if (string.IsNullOrWhiteSpace(_modelPath))
        {
            _unavailableReason = $"The {Target} detection model is not configured ({settingName}).";
            return null;
        }

        if (!File.Exists(_modelPath))
        {
            _unavailableReason = $"The {Target} detection model file is missing ({settingName}).";
            _logger.LogError("The {Target} detection model {Path} doesn't exist; media will go to review.", Target, _modelPath);
            return null;
        }

        try
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = threads,
            };

            // ffmpeg runs alongside on the same cores; idle ONNX Runtime threads mustn't spin and steal its CPU.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

            if (provider == RedactionExecutionProvider.Cuda)
            {
                // Only the Microsoft.ML.OnnxRuntime.Gpu package (a GPU worker image) has the CUDA provider; the CPU
                // package throws here, and the detector is then unavailable, so nothing is published unchecked.
                options.AppendExecutionProvider_CUDA(deviceId);
            }

            var session = new InferenceSession(_modelPath, options);
            _logger.LogInformation(
                "Loaded the {Target} detection model on {Provider} with {Threads} threads.", Target, provider, threads);
            return session;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException
            or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
        {
            _unavailableReason = provider == RedactionExecutionProvider.Cpu
                ? $"The {Target} detection model couldn't be loaded."
                : $"The {Target} detection model couldn't be loaded on {provider} (Media:Redaction:ExecutionProvider); is this the GPU worker image?";
            _logger.LogError(ex, "Couldn't load the {Target} detection model {Path} on {Provider}; media will go to review.", Target, _modelPath, provider);
            return null;
        }
    }
}

/// <summary>Resizing and model-input helpers for <see cref="RgbFrame"/>.</summary>
internal static class FrameOps
{
    /// <summary>Bilinear resize of a region of the frame (default: all of it) to the given size.</summary>
    public static RgbFrame Resize(RgbFrame source, int width, int height, int left = 0, int top = 0, int regionWidth = 0, int regionHeight = 0)
    {
        regionWidth = regionWidth <= 0 ? source.Width - left : regionWidth;
        regionHeight = regionHeight <= 0 ? source.Height - top : regionHeight;
        var result = new RgbFrame(width, height);
        var src = source.Pixels;
        var dst = result.Pixels;
        var scaleX = regionWidth / (float)width;
        var scaleY = regionHeight / (float)height;

        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp(((y + 0.5f) * scaleY) - 0.5f, 0, regionHeight - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, regionHeight - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp(((x + 0.5f) * scaleX) - 0.5f, 0, regionWidth - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, regionWidth - 1);
                var fx = sx - x0;
                var i00 = (((top + y0) * source.Width) + left + x0) * 3;
                var i01 = (((top + y0) * source.Width) + left + x1) * 3;
                var i10 = (((top + y1) * source.Width) + left + x0) * 3;
                var i11 = (((top + y1) * source.Width) + left + x1) * 3;
                var o = ((y * width) + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var upper = src[i00 + c] + ((src[i01 + c] - src[i00 + c]) * fx);
                    var lower = src[i10 + c] + ((src[i11 + c] - src[i10 + c]) * fx);
                    dst[o + c] = (byte)Math.Clamp((int)MathF.Round(upper + ((lower - upper) * fy)), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Planar RGB floats (0 to 255) of a <paramref name="size"/> x <paramref name="size"/> letterbox: the frame scaled
    /// to fit (by the returned scale) in the top left, the rest grey (114), as YOLOX expects.
    /// </summary>
    public static (float[] Data, float Scale) ToRgbLetterbox(RgbFrame frame, int size)
    {
        var scale = Math.Min(size / (float)frame.Width, size / (float)frame.Height);
        var w = Math.Clamp((int)(frame.Width * scale), 1, size);
        var h = Math.Clamp((int)(frame.Height * scale), 1, size);
        var resized = w == frame.Width && h == frame.Height ? frame : Resize(frame, w, h);
        var plane = size * size;
        var data = new float[plane * 3];
        Array.Fill(data, 114f);
        var pixels = resized.Pixels;
        for (var y = 0; y < h; y++)
        {
            var i = y * resized.Stride;
            var o = y * size;
            for (var x = 0; x < w; x++, i += 3, o++)
            {
                data[o] = pixels[i];
                data[plane + o] = pixels[i + 1];
                data[(2 * plane) + o] = pixels[i + 2];
            }
        }

        return (data, scale);
    }

    /// <summary>
    /// Planar BGR floats (0 to 255, the layout OpenCV's blobFromImage gives the YuNet models) of size
    /// <paramref name="width"/> x <paramref name="height"/>; the frame goes in the top left and the rest is zero.
    /// </summary>
    public static float[] ToBgrPlanes(RgbFrame frame, int width, int height)
    {
        var plane = width * height;
        var data = new float[plane * 3];
        var pixels = frame.Pixels;
        var rows = Math.Min(height, frame.Height);
        var cols = Math.Min(width, frame.Width);
        for (var y = 0; y < rows; y++)
        {
            var i = y * frame.Stride;
            var o = y * width;
            for (var x = 0; x < cols; x++, i += 3, o++)
            {
                data[o] = pixels[i + 2];
                data[plane + o] = pixels[i + 1];
                data[(2 * plane) + o] = pixels[i];
            }
        }

        return data;
    }
}
