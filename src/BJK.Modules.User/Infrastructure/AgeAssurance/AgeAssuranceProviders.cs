using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>The providers picked by <c>AgeAssurance:*:Provider</c> from everything registered.</summary>
public sealed class AgeAssuranceProviders(
    IEnumerable<ILivenessChecker> liveness,
    IEnumerable<IAgeEstimator> estimators,
    IEnumerable<IDocumentAgeCheck> documents,
    IOptionsMonitor<AgeAssuranceOptions> options)
{
    /// <summary>The selected liveness checker.</summary>
    public ILivenessChecker Liveness => Pick(liveness, options.CurrentValue.Liveness.Provider, "Liveness");

    /// <summary>The selected age estimator.</summary>
    public IAgeEstimator Estimator => Pick(estimators, options.CurrentValue.Estimator.Provider, "Estimator");

    /// <summary>The selected photo-ID check.</summary>
    public IDocumentAgeCheck Document => Pick(documents, options.CurrentValue.Document.Provider, "Document");

    private static T Pick<T>(IEnumerable<T> registered, string name, string section)
        where T : IAgeAssuranceProvider =>
        registered.LastOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No AgeAssurance:{section} provider called '{name}' is registered.");
}

/// <summary>
/// Checks the age check settings at startup: each selected provider must exist, development stand-ins are refused
/// outside the Development environment, and the HTTP estimator must have its endpoint and key.
/// </summary>
internal sealed class AgeAssuranceOptionsValidator(
    IHostEnvironment environment,
    IEnumerable<ILivenessChecker> liveness,
    IEnumerable<IAgeEstimator> estimators,
    IEnumerable<IDocumentAgeCheck> documents)
    : IValidateOptions<AgeAssuranceOptions>
{
    public ValidateOptionsResult Validate(string? name, AgeAssuranceOptions options)
    {
        var failures = new List<string>();

        if (options.ChallengeAge < Domain.AgeCheckRules.MinimumAge)
        {
            failures.Add($"AgeAssurance:ChallengeAge must be at least {Domain.AgeCheckRules.MinimumAge}.");
        }

        if (options.MaxAttemptsPerDay < 1)
        {
            failures.Add("AgeAssurance:MaxAttemptsPerDay must be at least 1.");
        }

        if (options.MaxVideoBytes < 1 || options.MaxImageBytes < 1)
        {
            failures.Add("AgeAssurance:MaxVideoBytes and AgeAssurance:MaxImageBytes must be positive.");
        }

        Check(liveness, options.Liveness.Provider, "Liveness", failures);
        Check(estimators, options.Estimator.Provider, "Estimator", failures);
        Check(documents, options.Document.Provider, "Document", failures);

        if (string.Equals(options.Estimator.Provider, AgeAssuranceOptions.HttpProvider, StringComparison.OrdinalIgnoreCase))
        {
            var http = options.Estimator.Http;
            if (http.Endpoint is not { IsAbsoluteUri: true } endpoint)
            {
                failures.Add("AgeAssurance:Estimator:Http:Endpoint is required (an absolute URL) when the estimator is Http.");
            }
            else if (endpoint.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
            {
                failures.Add("AgeAssurance:Estimator:Http:Endpoint must use HTTPS.");
            }

            if (string.IsNullOrWhiteSpace(http.ApiKey))
            {
                failures.Add("AgeAssurance:Estimator:Http:ApiKey is required when the estimator is Http.");
            }

            if (string.IsNullOrWhiteSpace(http.AgePath) || string.IsNullOrWhiteSpace(http.VideoField) || string.IsNullOrWhiteSpace(http.ApiKeyHeader))
            {
                failures.Add("AgeAssurance:Estimator:Http:AgePath, VideoField and ApiKeyHeader can't be empty.");
            }

            if (http.Timeout <= TimeSpan.Zero)
            {
                failures.Add("AgeAssurance:Estimator:Http:Timeout must be positive.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void Check<T>(IEnumerable<T> registered, string? provider, string section, List<string> failures)
        where T : IAgeAssuranceProvider
    {
        var match = registered.LastOrDefault(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var known = string.Join(", ", registered.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            failures.Add($"AgeAssurance:{section}:Provider '{provider}' isn't registered (available: {known}).");
        }
        else if (match.IsDevelopmentOnly && !environment.IsDevelopment())
        {
            failures.Add(
                $"AgeAssurance:{section}:Provider '{match.Name}' is for local development only and doesn't check anyone's age. "
                + $"Configure a real provider for the {environment.EnvironmentName} environment.");
        }
    }
}
