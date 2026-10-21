using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Configuration;

/// <summary>
/// A setting every deployed environment (anything but Development) must have, with a live value. Modules and hosts
/// add their own with <see cref="DeployedSettingsExtensions.RequireWhenDeployed"/>. Outside Development the app
/// refuses to start, and <c>--check-settings</c> fails, listing every one that's missing at once, so a deployment
/// can't quietly run with a stand-in (emails only logged, CAPTCHA test keys and so on).
/// </summary>
/// <param name="Key">The configuration key, e.g. <c>Email:SendGrid:ApiKey</c> (an array's first item is <c>Cors:AllowedOrigins:0</c>).</param>
/// <param name="Requirement">What it must be, for the message, e.g. "SendGrid (Log sends nothing)".</param>
/// <param name="IsValid">Checks the value; by default it just has to be set.</param>
/// <param name="AllowMissing">True when leaving it unset means a default that's fine live (only a set value is checked).</param>
public sealed record DeployedSettingRule(string Key, string Requirement, Func<string?, bool>? IsValid = null, bool AllowMissing = false);

/// <summary>Marker options: validating them runs every <see cref="DeployedSettingRule"/>.</summary>
public sealed class DeployedSettings;

/// <summary>Cloudflare Turnstile's public test keys, which accept (or reject) every token.</summary>
public static class TurnstileTestKeys
{
    /// <summary>True for Cloudflare's test site and secret keys (they start 1x0000, 2x0000 or 3x0000).</summary>
    public static bool IsTestKey(string? value) =>
        value is not null
        && (value.StartsWith("1x0000", StringComparison.Ordinal)
            || value.StartsWith("2x0000", StringComparison.Ordinal)
            || value.StartsWith("3x0000", StringComparison.Ordinal));
}

public static class DeployedSettingsExtensions
{
    /// <summary>
    /// Requires <paramref name="key"/> outside Development: set, not a template placeholder (&lt;...&gt;), and passing
    /// <paramref name="isValid"/> if given. Every failure is reported together when the app starts.
    /// </summary>
    public static IServiceCollection RequireWhenDeployed(
        this IServiceCollection services,
        string key,
        string requirement,
        Func<string?, bool>? isValid = null,
        bool allowMissing = false)
    {
        services.AddSingleton(new DeployedSettingRule(key, requirement, isValid, allowMissing));
        services.AddOptions<DeployedSettings>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DeployedSettings>, DeployedSettingsValidator>());
        return services;
    }
}

internal sealed class DeployedSettingsValidator(
    IEnumerable<DeployedSettingRule> rules,
    IConfiguration configuration,
    IHostEnvironment environment)
    : IValidateOptions<DeployedSettings>
{
    public ValidateOptionsResult Validate(string? name, DeployedSettings options)
    {
        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        foreach (var rule in rules.DistinctBy(r => (r.Key, r.Requirement)))
        {
            var value = configuration[rule.Key];
            if (string.IsNullOrWhiteSpace(value))
            {
                if (rule.AllowMissing)
                {
                    continue;
                }

                failures.Add($"{rule.Key} is missing: {rule.Requirement}.");
            }
            else if (IsPlaceholder(value))
            {
                failures.Add($"{rule.Key} is still a placeholder ({value}): {rule.Requirement}.");
            }
            else if (rule.IsValid is { } isValid && !isValid(value))
            {
                failures.Add($"{rule.Key} must be {rule.Requirement}.");
            }
        }

        // Any value still in the template's <...> form is a mistake outside Development, required or not.
        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (value is not null && IsPlaceholder(value) && !rules.Any(r => r.Key == key))
            {
                failures.Add($"{key} is still a placeholder ({value}).");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                [$"{failures.Count} setting(s) aren't ready for a deployed environment ({environment.EnvironmentName}):", .. failures]);
    }

    /// <summary>True for an absolute https address.</summary>
    internal static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsPlaceholder(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 2 && trimmed[0] == '<' && trimmed[^1] == '>';
    }
}

/// <summary>
/// <c>--check-settings</c>: build the host, run every startup settings check (including
/// <see cref="DeployedSettingRule"/>s), print the result and exit, without touching the database. Deploy pipelines run
/// the image with an environment's settings and stop if it fails.
/// </summary>
public static class SettingsCheck
{
    public const string Argument = "--check-settings";

    public static bool Requested(IEnumerable<string> args) => args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs the checks; 0 when every setting is ready, 1 otherwise (each problem on its own line).</summary>
    public static int Run(IServiceProvider services)
    {
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
            Console.WriteLine("Settings check passed.");
            return 0;
        }
        catch (Exception ex) when (ex is OptionsValidationException or AggregateException)
        {
            var failures = ex is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures)
                : ((OptionsValidationException)ex).Failures;
            Console.Error.WriteLine("Settings check failed:");
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($"  {failure}");
            }

            return 1;
        }
    }
}

/// <summary>Common checks for <see cref="DeployedSettingsExtensions.RequireWhenDeployed"/>.</summary>
public static class DeployedSettingChecks
{
    /// <summary>An absolute https address.</summary>
    public static bool IsHttps(string? value) => DeployedSettingsValidator.IsHttps(value);
}
