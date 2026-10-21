using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>
/// The provider for each step of the age check: the real one whose settings are filled in; without one, the stand-in
/// in Development. Outside Development there's no fallback: startup has already refused to run without a real
/// provider (<see cref="AgeAssuranceOptionsValidator"/>), and a missing one here throws rather than passing anyone.
/// </summary>
public sealed class AgeAssuranceProviders(
    IEnumerable<ILivenessChecker> liveness,
    IEnumerable<IAgeEstimator> estimators,
    IEnumerable<IDocumentAgeCheck> documents,
    IHostEnvironment environment)
{
    /// <summary>The liveness checker.</summary>
    public ILivenessChecker Liveness => Pick(liveness, environment, "Liveness");

    /// <summary>The age estimator.</summary>
    public IAgeEstimator Estimator => Pick(estimators, environment, "Estimator");

    /// <summary>The photo-ID check.</summary>
    public IDocumentAgeCheck Document => Pick(documents, environment, "Document");

    internal static T? Find<T>(IEnumerable<T> registered, IHostEnvironment environment)
        where T : IAgeAssuranceProvider =>
        registered.LastOrDefault(p => !p.IsDevelopmentOnly && p.IsConfigured)
        ?? (environment.IsDevelopment() ? registered.LastOrDefault(p => p.IsDevelopmentOnly) : default);

    private static T Pick<T>(IEnumerable<T> registered, IHostEnvironment environment, string step)
        where T : IAgeAssuranceProvider =>
        Find(registered, environment)
        ?? throw new InvalidOperationException($"The age check has no {step} provider set up ({AgeAssuranceOptionsValidator.Needs(step)}).");
}

/// <summary>
/// Checks the age check settings at startup. Outside Development every step needs a real provider with its settings
/// filled in; the stand-ins are never used there.
/// </summary>
/// <remarks>
/// The providers are looked up when validating, not injected: they depend on these options themselves, so injecting
/// them would be a dependency cycle.
/// </remarks>
internal sealed class AgeAssuranceOptionsValidator(IHostEnvironment environment, IServiceProvider services)
    : IValidateOptions<AgeAssuranceOptions>
{
    public ValidateOptionsResult Validate(string? name, AgeAssuranceOptions options)
    {
        var failures = new List<string>();
        if (options.ChallengeAge < Domain.AgeCheckRules.AdultAge)
        {
            failures.Add($"AgeAssurance:ChallengeAge must be at least {Domain.AgeCheckRules.AdultAge}.");
        }

        if (options.MaxAttemptsPerDay < 1)
        {
            failures.Add("AgeAssurance:MaxAttemptsPerDay must be at least 1.");
        }

        if (options.MaxVideoBytes < 1 || options.MaxImageBytes < 1)
        {
            failures.Add("AgeAssurance:MaxVideoBytes and AgeAssurance:MaxImageBytes must be positive.");
        }

        var yoti = options.Yoti;
        if (yoti.IsConfigured)
        {
            if (!Guid.TryParse(yoti.SdkId, out _))
            {
                failures.Add("AgeAssurance:Yoti:SdkId must be the SDK ID from the Yoti Hub (a UUID).");
            }

            if (!YotiRequestSigner.TryLoad(yoti.PrivateKey, out _))
            {
                failures.Add("AgeAssurance:Yoti:PrivateKey must be the PEM private key from the Yoti Hub (the whole -----BEGIN ... text).");
            }

            if (yoti.BaseUrl is not { IsAbsoluteUri: true } baseUrl || (baseUrl.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment()))
            {
                failures.Add("AgeAssurance:Yoti:BaseUrl must be an https address.");
            }

            if (yoti.Timeout <= TimeSpan.Zero || yoti.MaxUncertainty <= 0)
            {
                failures.Add("AgeAssurance:Yoti:Timeout and MaxUncertainty must be positive.");
            }
        }

        if (!environment.IsDevelopment())
        {
            Require(services.GetServices<ILivenessChecker>(), "Liveness", failures);
            // Yoti is judged from the settings being validated: asking the estimator would read these options while
            // they're still being validated.
            Require(services.GetServices<IAgeEstimator>().Where(e => e is not YotiAgeEstimator), "Estimator", failures, options.Yoti.IsConfigured);
            Require(services.GetServices<IDocumentAgeCheck>(), "Document", failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>What a step needs, for messages.</summary>
    internal static string Needs(string step) => step switch
    {
        "Liveness" => "set AgeAssurance:Liveness:ModelPath to the YuNet model",
        "Estimator" => "set AgeAssurance:Yoti:SdkId and PrivateKey",
        _ => "no photo ID provider is built yet: Yoti's photo ID check is still to do",
    };

    private void Require<T>(IEnumerable<T> registered, string step, List<string> failures, bool alreadySetUp = false)
        where T : IAgeAssuranceProvider
    {
        if (!alreadySetUp && AgeAssuranceProviders.Find(registered, environment) is null)
        {
            failures.Add(
                $"The age check's {step} step has no real provider in the {environment.EnvironmentName} environment "
                + $"(the stand-ins only run in Development): {Needs(step)}.");
        }
    }
}
