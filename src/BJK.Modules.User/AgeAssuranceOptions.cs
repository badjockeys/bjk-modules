namespace BJK.Modules.User;

/// <summary>
/// Age check settings, section <c>AgeAssurance</c>. Each <c>Provider</c> picks an implementation by name.
/// <c>Development</c> providers don't check anything real and the app refuses to start with them outside the
/// Development environment.
/// </summary>
public sealed class AgeAssuranceOptions
{
    public const string SectionName = "AgeAssurance";

    /// <summary>The development stand-ins' name.</summary>
    public const string DevelopmentProvider = "Development";

    /// <summary>The generic HTTP age estimation adapter's name.</summary>
    public const string HttpProvider = "Http";

    /// <summary>Estimated age that passes without photo ID ("Challenge 25"). Default 25; at least 18.</summary>
    public int ChallengeAge { get; set; } = 25;

    /// <summary>Attempts (selfie or photo ID) allowed in 24 hours. Default 3.</summary>
    public int MaxAttemptsPerDay { get; set; } = 3;

    /// <summary>Largest selfie video accepted, in bytes. Default 15 MB (a 3–5 second clip is usually under 2 MB).</summary>
    public long MaxVideoBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>Largest ID photo accepted, in bytes. Default 10 MB.</summary>
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Section <c>AgeAssurance:Liveness</c>.</summary>
    public LivenessProviderOptions Liveness { get; set; } = new();

    /// <summary>Section <c>AgeAssurance:Estimator</c>.</summary>
    public EstimatorProviderOptions Estimator { get; set; } = new();

    /// <summary>Section <c>AgeAssurance:Document</c>.</summary>
    public DocumentProviderOptions Document { get; set; } = new();
}

/// <summary>Section <c>AgeAssurance:Liveness</c>.</summary>
public sealed class LivenessProviderOptions
{
    /// <summary>
    /// <c>YuNet</c>: open-source face detection on our servers (registered by the identity server).
    /// <c>Development</c> (default): accepts any non-empty video, Development only.
    /// </summary>
    public string Provider { get; set; } = AgeAssuranceOptions.DevelopmentProvider;

    /// <summary>Section <c>AgeAssurance:Liveness:Development</c>.</summary>
    public DevelopmentLivenessOptions Development { get; set; } = new();
}

/// <summary>Section <c>AgeAssurance:Liveness:Development</c>.</summary>
public sealed class DevelopmentLivenessOptions
{
    /// <summary>False makes every liveness check fail, to try that path locally. Default true.</summary>
    public bool Pass { get; set; } = true;
}

/// <summary>Section <c>AgeAssurance:Estimator</c>.</summary>
public sealed class EstimatorProviderOptions
{
    /// <summary><c>Http</c>: a specialist provider's API. <c>Development</c> (default): returns a set age, Development only.</summary>
    public string Provider { get; set; } = AgeAssuranceOptions.DevelopmentProvider;

    /// <summary>Section <c>AgeAssurance:Estimator:Development</c>.</summary>
    public DevelopmentAgeOptions Development { get; set; } = new();

    /// <summary>Section <c>AgeAssurance:Estimator:Http</c>.</summary>
    public HttpAgeEstimatorOptions Http { get; set; } = new();
}

/// <summary>Section <c>AgeAssurance:Document</c>.</summary>
public sealed class DocumentProviderOptions
{
    /// <summary>
    /// <c>Development</c> (default): returns a set age, Development only. A real photo-ID provider is plugged in by
    /// registering another <see cref="Infrastructure.AgeAssurance.IDocumentAgeCheck"/>.
    /// </summary>
    public string Provider { get; set; } = AgeAssuranceOptions.DevelopmentProvider;

    /// <summary>Section <c>AgeAssurance:Document:Development</c>.</summary>
    public DevelopmentAgeOptions Development { get; set; } = new();
}

/// <summary>What a development stand-in answers.</summary>
public sealed class DevelopmentAgeOptions
{
    /// <summary>The age every check returns. Default 30 (passes); try 20 (photo ID needed) or 15 (under 18). 0 = couldn't tell.</summary>
    public int Age { get; set; } = 30;
}

/// <summary>
/// Section <c>AgeAssurance:Estimator:Http</c>: a generic adapter for a facial age estimation API. It POSTs the video
/// as multipart/form-data and reads the age (and optionally a margin of error) from the JSON response.
/// </summary>
public sealed class HttpAgeEstimatorOptions
{
    /// <summary>The provider's name, recorded with each result, e.g. "Yoti". Default "Http".</summary>
    public string Name { get; set; } = AgeAssuranceOptions.HttpProvider;

    /// <summary>The estimation endpoint (HTTPS outside Development).</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Secret. Sent in <see cref="ApiKeyHeader"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header carrying the key. Default <c>Authorization</c>.</summary>
    public string ApiKeyHeader { get; set; } = "Authorization";

    /// <summary>Put before the key in the header. Default <c>Bearer </c>; empty for providers that take the bare key.</summary>
    public string ApiKeyPrefix { get; set; } = "Bearer ";

    /// <summary>Form field the video goes in. Default <c>video</c>.</summary>
    public string VideoField { get; set; } = "video";

    /// <summary>Where the estimated age is in the JSON response, as a dot path, e.g. <c>age</c> or <c>result.age.estimate</c>.</summary>
    public string AgePath { get; set; } = "age";

    /// <summary>Optional dot path to a margin of error in years (e.g. a standard deviation). Empty = none.</summary>
    public string? UncertaintyPath { get; set; }

    /// <summary>How long one request may take. Default 20 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
}
