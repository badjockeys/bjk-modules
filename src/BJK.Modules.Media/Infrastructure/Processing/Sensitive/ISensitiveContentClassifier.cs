using BJK.Modules.Media.Infrastructure.Processing.Redaction;

namespace BJK.Modules.Media.Infrastructure.Processing.Sensitive;

/// <summary>
/// Scores a picture for graphic content (blood, injuries, the aftermath of serious crashes), so such media can be
/// marked sensitive automatically before it's published, and (if the classifier can) for nudity, so such media goes to
/// a moderator before it's published. Swappable: <see cref="ClipContentClassifier"/> (the default) scores both with
/// one CLIP model; <see cref="OnnxSensitiveContentClassifier"/> runs any image classification model in ONNX format
/// (graphic content only); <see cref="DevelopmentSensitiveContentClassifier"/> is a stand-in for local testing.
/// Implementations must be safe to call from several threads.
/// </summary>
public interface ISensitiveContentClassifier
{
    /// <summary>False if the model isn't configured or couldn't be loaded; <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the classifier can't run, for moderators and logs. Null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// How likely the picture shows graphic content, 0 to 1. Throws if the model fails; the caller then sends the media
    /// to review (it's never published unchecked).
    /// </summary>
    /// <param name="frame">The picture, at any size (the blurring pass's scaled-down copy for videos).</param>
    float Score(RgbFrame frame);

    /// <summary>
    /// Every score this classifier gives for the picture: graphic content, and nudity if it can tell (null if not).
    /// Throws as <see cref="Score"/> does.
    /// </summary>
    /// <param name="frame">The picture.</param>
    ContentScores Classify(RgbFrame frame) => new(Score(frame), null);
}

/// <summary>A picture's scores, 0 to 1: graphic content (blood, injuries) and nudity or sexual content (null if not checked).</summary>
public readonly record struct ContentScores(float Graphic, float? Nudity);
