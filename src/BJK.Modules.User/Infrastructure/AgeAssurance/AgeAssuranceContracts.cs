namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>
/// A provider used by the age check. Several can be registered; <c>AgeAssurance:*:Provider</c> picks one by
/// <see cref="Name"/>, and startup refuses development-only providers outside the Development environment.
/// </summary>
public interface IAgeAssuranceProvider
{
    /// <summary>The name used in settings to select this provider, e.g. "Development", "YuNet" or "Http".</summary>
    string Name { get; }

    /// <summary>True for stand-ins that don't really check anything. Never allowed outside Development.</summary>
    bool IsDevelopmentOnly { get; }
}

/// <summary>
/// Checks that the selfie video shows one live person turning their head (left, then right), so a photo held up to
/// the camera doesn't pass. Runs on our own servers with open-source models; the identity server registers the
/// real implementation (YuNet face detection via ONNX Runtime).
/// </summary>
public interface ILivenessChecker : IAgeAssuranceProvider
{
    /// <summary>Checks the video. Implementations must not keep, log or store the video.</summary>
    /// <param name="video">The recorded selfie, held in memory.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<LivenessResult> CheckAsync(SelfieVideo video, CancellationToken cancellationToken);
}

/// <summary>
/// Estimates age from the selfie video. A specialist provider behind an HTTP API (<see cref="HttpAgeEstimator"/>),
/// or the development stand-in.
/// </summary>
public interface IAgeEstimator : IAgeAssuranceProvider
{
    /// <summary>Estimates the person's age. Implementations must not keep, log or store the video.</summary>
    /// <param name="video">The recorded selfie, held in memory.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<AgeEstimate> EstimateAsync(SelfieVideo video, CancellationToken cancellationToken);
}

/// <summary>Photo-ID fallback: reads the date of birth from a photo of a government ID.</summary>
public interface IDocumentAgeCheck : IAgeAssuranceProvider
{
    /// <summary>Checks the ID. Implementations must not keep, log or store the image or anything read from it.</summary>
    /// <param name="image">The ID photo, held in memory.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<DocumentAgeResult> CheckAsync(DocumentImage image, CancellationToken cancellationToken);
}

/// <summary>A selfie video recorded in the browser, only ever held in memory (or a temp file deleted straight after use).</summary>
/// <param name="Content">The video bytes.</param>
/// <param name="ContentType">E.g. video/webm or video/mp4.</param>
public sealed record SelfieVideo(ReadOnlyMemory<byte> Content, string ContentType)
{
    /// <summary>Never prints the bytes.</summary>
    public override string ToString() => $"SelfieVideo({ContentType}, {Content.Length} bytes)";
}

/// <summary>A photo of an ID, only ever held in memory.</summary>
/// <param name="Content">The image bytes.</param>
/// <param name="ContentType">E.g. image/jpeg.</param>
public sealed record DocumentImage(ReadOnlyMemory<byte> Content, string ContentType)
{
    /// <summary>Never prints the bytes.</summary>
    public override string ToString() => $"DocumentImage({ContentType}, {Content.Length} bytes)";
}

/// <summary>Why a liveness check failed. The user sees a matching hint.</summary>
public enum LivenessFailure
{
    /// <summary>The video couldn't be read.</summary>
    Unreadable,

    /// <summary>No face (or not enough frames with a face) was seen.</summary>
    NoFace,

    /// <summary>More than one face was seen.</summary>
    MultipleFaces,

    /// <summary>A face was seen, but it didn't turn one way and then the other.</summary>
    NoHeadTurn,
}

/// <summary>Outcome of a liveness check.</summary>
/// <param name="IsLive">True if the video showed one live person turning their head.</param>
/// <param name="Failure">Why not, when <paramref name="IsLive"/> is false.</param>
public sealed record LivenessResult(bool IsLive, LivenessFailure? Failure = null)
{
    /// <summary>A pass.</summary>
    public static LivenessResult Live { get; } = new(true);

    /// <summary>A failure, with the reason.</summary>
    /// <param name="failure">Why it failed.</param>
    public static LivenessResult Failed(LivenessFailure failure) => new(false, failure);
}

/// <summary>An age estimate. Only the pass/fail decision made from it is stored, never the number.</summary>
/// <param name="Age">Estimated age in years, or null if the provider couldn't estimate.</param>
/// <param name="Uncertainty">Margin of error in years either side (0 if the provider gives none).</param>
/// <param name="Provider">Name to record against the result, e.g. "Development" or the vendor's name.</param>
public sealed record AgeEstimate(double? Age, double Uncertainty, string Provider);

/// <summary>Outcome of a photo-ID check. Only pass/fail is stored, never the date of birth.</summary>
/// <param name="Age">Age from the date of birth on the ID, or null if it couldn't be read.</param>
/// <param name="Provider">Name to record against the result.</param>
public sealed record DocumentAgeResult(int? Age, string Provider);
