using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure.Email;
using BJK.Modules.Communications.Infrastructure.Http;
using BJK.Modules.Communications.Infrastructure.Sms;
using BJK.Modules.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Communications;

/// <summary>
/// Registers <see cref="IEmailSender"/> and <see cref="ISmsSender"/>. No database, no endpoints: other modules and the
/// hosts inject the senders. The provider for each is chosen by <c>Email:Provider</c> and <c>Sms:Provider</c>; both
/// default to <c>Log</c>, and the settings are checked at startup.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"), new CommunicationsModule());
/// </code>
/// </example>
public sealed class CommunicationsModule : IModule
{
    /// <summary>How long one provider request may take before it's retried.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Name => "Communications";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SmsOptions>()
            .Bind(configuration.GetSection(SmsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmsOptions>, SmsOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ProviderRetry(sp.GetRequiredService<TimeProvider>()));

        var emailProvider = configuration[$"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)}"] ?? EmailOptions.LogProvider;
        if (emailProvider == EmailOptions.SendGridProvider)
        {
            services.AddHttpClient<IEmailSender, SendGridEmailSender>(client => client.Timeout = RequestTimeout);
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        var smsProvider = configuration[$"{SmsOptions.SectionName}:{nameof(SmsOptions.Provider)}"] ?? SmsOptions.LogProvider;
        if (smsProvider == SmsOptions.TwilioProvider)
        {
            services.AddHttpClient<ISmsSender, TwilioSmsSender>(client => client.Timeout = RequestTimeout);
        }
        else
        {
            services.AddSingleton<ISmsSender, LoggingSmsSender>();
        }
    }
}
