using BJK.Modules.Shared;
using BJK.Modules.Shared.Delivery;
using BJK.Modules.SMS.Contracts;
using BJK.Modules.SMS.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.SMS;

/// <summary>
/// Registers <see cref="ISMSSender"/>. No database, no endpoints: other modules and the hosts inject the sender and
/// build messages from <see cref="Templates.SMSTemplates"/>. <c>SMS:Provider</c> picks Twilio or the console
/// (<c>Log</c>, the default); the settings are checked at startup.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"), new SMSModule());
/// </code>
/// </example>
public sealed class SMSModule : IModule
{
    /// <summary>How long one Twilio request may take before it's retried.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Name => "SMS";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SMSOptions>()
            .Bind(configuration.GetSection(SMSOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SMSOptions>, SMSOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ProviderRetry(sp.GetRequiredService<TimeProvider>()));

        var provider = configuration[$"{SMSOptions.SectionName}:{nameof(SMSOptions.Provider)}"] ?? SMSOptions.LogProvider;
        if (provider == SMSOptions.TwilioProvider)
        {
            services.AddHttpClient<ISMSSender, TwilioSMSSender>(client => client.Timeout = RequestTimeout);
        }
        else
        {
            services.AddSingleton<ISMSSender, LoggingSMSSender>();
        }
    }
}
