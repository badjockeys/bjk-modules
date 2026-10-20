using BJK.Modules.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Safety;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IContentScreener"/> from <c>Screening:Provider</c>, with its settings checked at startup.
    /// Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddContentScreening(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(s => s.ServiceType == typeof(KeywordContentScreener)))
        {
            return services;
        }

        services.AddOptions<ScreeningOptions>()
            .Bind(configuration.GetSection(ScreeningOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ScreeningOptions>, ScreeningOptionsValidator>());
        services.AddSingleton<KeywordContentScreener>();

        // Text with an address on the blocked list (IBlockedUrlList, if a module registers one) is always held.
        var provider = configuration[$"{ScreeningOptions.SectionName}:{nameof(ScreeningOptions.Provider)}"] ?? ScreeningOptions.KeywordsProvider;
        if (provider == ScreeningOptions.HttpProvider)
        {
            services.AddHttpClient<HttpContentScreener>((sp, client) =>
                client.Timeout = sp.GetRequiredService<IOptions<ScreeningOptions>>().Value.Timeout);
            services.AddTransient<IContentScreener>(sp => WithBlockedUrls(sp, sp.GetRequiredService<HttpContentScreener>()));
        }
        else
        {
            services.AddSingleton<IContentScreener>(sp => WithBlockedUrls(sp, sp.GetRequiredService<KeywordContentScreener>()));
        }

        return services;
    }

    private static IContentScreener WithBlockedUrls(IServiceProvider services, IContentScreener inner) =>
        services.GetService<IBlockedUrlList>() is { } list ? new BlockedUrlScreener(inner, list) : inner;

    /// <summary>
    /// Registers <see cref="ICaptchaVerifier"/> from <c>Captcha:Provider</c>, with its settings checked at startup.
    /// Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddCaptchaVerification(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(s => s.ServiceType == typeof(IValidateOptions<CaptchaOptions>)))
        {
            return services;
        }

        services.AddOptions<CaptchaOptions>()
            .Bind(configuration.GetSection(CaptchaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CaptchaOptions>, CaptchaOptionsValidator>();
        services.RequireWhenDeployed(
            "Captcha:Provider",
            "Turnstile (AlwaysPass checks nothing)",
            v => v == CaptchaOptions.TurnstileProvider,
            allowMissing: true);
        services.RequireWhenDeployed(
            "Captcha:TurnstileSecretKey",
            "the real Turnstile secret key, not one of Cloudflare's test keys",
            v => !TurnstileTestKeys.IsTestKey(v));

        var provider = configuration[$"{CaptchaOptions.SectionName}:{nameof(CaptchaOptions.Provider)}"] ?? CaptchaOptions.TurnstileProvider;
        if (provider == CaptchaOptions.AlwaysPassProvider)
        {
            services.AddSingleton<ICaptchaVerifier, AlwaysPassCaptchaVerifier>();
        }
        else
        {
            services.AddHttpClient<ICaptchaVerifier, TurnstileCaptchaVerifier>(http => http.Timeout = TimeSpan.FromSeconds(10));
        }

        return services;
    }

    /// <summary>Binds <see cref="SafetyOptions"/> (section <c>Safety</c>). Called by <c>AddBjkModules</c>.</summary>
    internal static IServiceCollection AddSafetyOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SafetyOptions>()
            .Bind(configuration.GetSection(SafetyOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SafetyOptions>, SafetyOptionsValidator>());
        return services;
    }
}
