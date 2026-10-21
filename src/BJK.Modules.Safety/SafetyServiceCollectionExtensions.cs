using BJK.Modules.Shared.Configuration;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Safety;

public static class SafetyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IContentScreener"/> (keywords, plus the classifier at <c>Screening:Endpoint</c> when set), with its settings checked at startup.
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
        if (!string.IsNullOrWhiteSpace(configuration[$"{ScreeningOptions.SectionName}:{nameof(ScreeningOptions.Endpoint)}"]))
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
    /// Registers <see cref="ICaptchaVerifier"/> (Cloudflare Turnstile), with its settings checked at startup.
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
            "Captcha:TurnstileSecretKey",
            "the real Turnstile secret key, not one of Cloudflare's test keys",
            v => !TurnstileTestKeys.IsTestKey(v));

        services.AddHttpClient<ICaptchaVerifier, TurnstileCaptchaVerifier>(http => http.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }

    /// <summary>
    /// Binds <see cref="SafetyOptions"/> (section <c>Safety</c>) and adds the age check for
    /// <see cref="IRequiresAgeVerification"/> commands: a MediatR step that runs before validation, and the 403
    /// problem response (<c>code</c> = <see cref="AgeVerificationRequiredException.Code"/>). Call it from a module's
    /// <c>AddServices</c>, which runs before <c>AddBjkModules</c> registers MediatR and the shared error handler, so
    /// both come first. Safe to call more than once.
    /// </summary>
    public static IServiceCollection AddAgeVerification(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(s => s.ServiceType == typeof(IValidateOptions<SafetyOptions>)))
        {
            return services;
        }

        services.AddOptions<SafetyOptions>()
            .Bind(configuration.GetSection(SafetyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SafetyOptions>, SafetyOptionsValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AgeVerificationBehaviour<,>));
        services.AddExceptionHandler<AgeVerificationExceptionHandler>();
        return services;
    }
}
