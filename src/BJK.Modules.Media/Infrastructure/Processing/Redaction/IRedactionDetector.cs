namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>What a detector looks for.</summary>
public enum RedactionTarget
{
    Face,
    NumberPlate,

    /// <summary>
    /// The head of a person found by the person detector: a backup for faces the face model can't see (dark, side-on,
    /// turned away, far off). Blurred like a face, but never sends media to review on its own.
    /// </summary>
    Head,

    /// <summary>
    /// Where a vehicle's number plate would be (the lower middle of a car, van, bus, lorry or motorbike the person
    /// detector found), blurred when the plate model found no plate there. The plate model misses many UK plates,
    /// especially white front plates in headlight glare. Never sends media to review on its own.
    /// </summary>
    PlateArea,
}

/// <summary>One picture in memory: 8-bit RGB, rows top to bottom, no padding between rows.</summary>
public sealed class RgbFrame
{
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="pixels">Width x height x 3 bytes; a new buffer is made if null.</param>
    public RgbFrame(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var size = checked(width * height * 3);
        if (pixels is not null && pixels.Length < size)
        {
            throw new ArgumentException($"Expected at least {size} bytes.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[size];
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 3;
}

/// <summary>A rectangle in pixels, with how sure the detector was (0 to 1; 1 for manual regions).</summary>
/// <param name="Target">Face or number plate.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
/// <param name="Confidence">Detector score.</param>
public readonly record struct DetectionBox(RedactionTarget Target, float X, float Y, float Width, float Height, float Confidence)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public float Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// Finds faces or number plates in one frame. Implementations run open-source ONNX models on the CPU with ONNX
/// Runtime. They must be safe to call from several threads.
/// </summary>
public interface IRedactionDetector
{
    RedactionTarget Target { get; }

    /// <summary>False if the model isn't configured or couldn't be loaded; <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the detector can't run, for moderators and logs. Null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Every candidate scoring at least <paramref name="minConfidence"/>, in the frame's pixel coordinates.
    /// Throws if the model fails; the caller then sends the media to review.
    /// </summary>
    /// <param name="frame">The picture.</param>
    /// <param name="minConfidence">Lowest score to return.</param>
    IReadOnlyList<DetectionBox> Detect(RgbFrame frame, float minConfidence);
}
