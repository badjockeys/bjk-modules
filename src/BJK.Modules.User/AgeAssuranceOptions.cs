namespace BJK.Modules.User;

/// <summary>
/// Settings for the on-demand age check for sensitive posts (section <c>AgeAssurance</c>): the
/// <see cref="Liveness"/> check against spoofing, then Yoti's face age estimate, with photo ID as the fallback. There
/// are no provider switches: a real check is used when its settings are filled in (<see cref="Liveness"/>'s model,
/// <see cref="Yoti"/>'s keys). When they're empty, the Development environment uses stand-ins that answer
/// <see cref="Development"/>'s values (and logs that it does), and anywhere else the app refuses to start.
/// </summary>
public sealed class AgeAssuranceOptions
{
    public const string SectionName = "AgeAssurance";

    /// <summary>The name the stand-ins record against results.</summary>
    public const string DevelopmentProvider = "Development";

    /// <summary>Estimated age that passes without photo ID ("Challenge 25"). Default 25; at least 18.</summary>
    public int ChallengeAge { get; set; } = 25;

    /// <summary>Attempts (selfie or photo ID) allowed in 24 hours. Default 3.</summary>
    public int MaxAttemptsPerDay { get; set; } = 3;

    /// <summary>Largest selfie video accepted, in bytes. Default 15 MB (a 3–5 second clip is usually under 2 MB).</summary>
    public long MaxVideoBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Largest ID photo accepted, in bytes. Default 10 MB.</summary>
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Section <c>AgeAssurance:Liveness</c>: the head-turn check (YuNet, run by the identity server).</summary>
    public LivenessOptions Liveness { get; set; } = new();

    /// <summary>Section <c>AgeAssurance:Yoti</c>: the face age estimate.</summary>
    public YotiOptions Yoti { get; set; } = new();

    /// <summary>Section <c>AgeAssurance:Development</c>: what the local stand-ins answer. Ignored outside Development.</summary>
    public DevelopmentAgeAssuranceOptions Development { get; set; } = new();
}

/// <summary>Section <c>AgeAssurance:Liveness</c>.</summary>
public sealed class LivenessOptions
{
    /// <summary>
    /// Path to OpenCV Zoo's <c>face_detection_yunet_2023mar.onnx</c> (MIT licence, about 230 KB). Set: the real
    /// head-turn check runs. Empty: the stand-in locally; outside Development the app won't start.
    /// </summary>
    /// <remarks>The identity server reads the rest of this section (frame rate, thresholds) for its YuNet checker.</remarks>
    public string? ModelPath { get; set; }
}

/// <summary>
/// Section <c>AgeAssurance:Yoti</c>: Yoti's facial age estimation (with Yoti's own anti-spoofing). Used when
/// <see cref="SdkId"/> and <see cref="PrivateKey"/> are set. One frame of the selfie video is sent, never the video.
/// </summary>
public sealed class YotiOptions
{
    /// <summary>The SDK ID of the application in the Yoti Hub.</summary>
    public string? SdkId { get; set; }

    /// <summary>
    /// Secret. The PEM private key Yoti issues with the SDK ID (the whole <c>-----BEGIN ...</c> text), used to sign every
    /// request. A container secret when deployed.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>Yoti's AI services address. Default <c>https://api.yoti.com/ai/v1</c>.</summary>
    public Uri BaseUrl { get; set; } = new("https://api.yoti.com/ai/v1");

    /// <summary>How long one request may take. Default 20 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Largest margin of error (Yoti's <c>st_dev</c>, in years) accepted; above it the result counts as "couldn't
    /// tell" and photo ID is offered. Default 6, as Yoti recommends.
    /// </summary>
    public double MaxUncertainty { get; set; } = 6.0;

    /// <summary>ffmpeg, used to take one frame from the selfie video. Default <c>ffmpeg</c> on the PATH.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>True when the keys are set (template placeholders don't count).</summary>
    public bool IsConfigured => IsSet(SdkId) && IsSet(PrivateKey);

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('<');
}

/// <summary>Section <c>AgeAssurance:Development</c>: what the stand-ins answer locally. Ignored outside Development.</summary>
public sealed class DevelopmentAgeAssuranceOptions
{
    /// <summary>False makes every liveness check fail, to try that path. Default true.</summary>
    public bool LivenessPasses { get; set; } = true;

    /// <summary>The age every face estimate returns. Default 30 (passes); try 20 (photo ID needed), 15 (under 18), 0 (couldn't tell).</summary>
    public int EstimatedAge { get; set; } = 30;

    /// <summary>The age every photo ID check returns. Default 30; 16 = under 18, 0 = unreadable.</summary>
    public int DocumentAge { get; set; } = 30;
}
