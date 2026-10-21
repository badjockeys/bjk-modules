using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>
/// DEVELOPMENT ONLY. Used when <c>AgeAssurance:Liveness:ModelPath</c> isn't set. Accepts any non-empty video (or
/// fails every one, with <c>AgeAssurance:Development:LivenessPasses=false</c>). Never used outside Development.
/// </summary>
internal sealed class DevelopmentLivenessChecker(IOptionsMonitor<AgeAssuranceOptions> options, ILogger<DevelopmentLivenessChecker> logger)
    : ILivenessChecker
{
    public string Name => AgeAssuranceOptions.DevelopmentProvider;

    public bool IsDevelopmentOnly => true;

    public bool IsConfigured => true;

    public Task<LivenessResult> CheckAsync(SelfieVideo video, CancellationToken cancellationToken)
    {
        logger.LogWarning("DEVELOPMENT ONLY - liveness not really checked.");
        if (video.Content.IsEmpty)
        {
            return Task.FromResult(LivenessResult.Failed(LivenessFailure.Unreadable));
        }

        return Task.FromResult(options.CurrentValue.Development.LivenessPasses
            ? LivenessResult.Live
            : LivenessResult.Failed(LivenessFailure.NoHeadTurn));
    }
}

/// <summary>
/// DEVELOPMENT ONLY. Used when Yoti isn't set up. Returns <c>AgeAssurance:Development:EstimatedAge</c> for every
/// video (0 = couldn't tell). Never used outside Development.
/// </summary>
internal sealed class DevelopmentAgeEstimator(IOptionsMonitor<AgeAssuranceOptions> options, ILogger<DevelopmentAgeEstimator> logger)
    : IAgeEstimator
{
    public string Name => AgeAssuranceOptions.DevelopmentProvider;

    public bool IsDevelopmentOnly => true;

    public bool IsConfigured => true;

    public Task<AgeEstimate> EstimateAsync(SelfieVideo video, CancellationToken cancellationToken)
    {
        logger.LogWarning("DEVELOPMENT ONLY - age not really estimated.");
        var age = options.CurrentValue.Development.EstimatedAge;
        return Task.FromResult(new AgeEstimate(age > 0 ? age : null, 0, Name));
    }
}

/// <summary>
/// DEVELOPMENT ONLY. Returns <c>AgeAssurance:Development:DocumentAge</c> for every ID photo (0 = unreadable). Never
/// used outside Development.
/// </summary>
internal sealed class DevelopmentDocumentAgeCheck(IOptionsMonitor<AgeAssuranceOptions> options, ILogger<DevelopmentDocumentAgeCheck> logger)
    : IDocumentAgeCheck
{
    public string Name => AgeAssuranceOptions.DevelopmentProvider;

    public bool IsDevelopmentOnly => true;

    public bool IsConfigured => true;

    public Task<DocumentAgeResult> CheckAsync(DocumentImage image, CancellationToken cancellationToken)
    {
        logger.LogWarning("DEVELOPMENT ONLY - photo ID not really checked.");
        var age = options.CurrentValue.Development.DocumentAge;
        return Task.FromResult(new DocumentAgeResult(age > 0 && !image.Content.IsEmpty ? age : null, Name));
    }
}
