using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Infrastructure;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Delivery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Email;

/// <summary>
/// Registers <see cref="IEmailSender"/>. No database, no endpoints: other modules and the hosts inject the sender
/// and build messages from <see cref="Templates.EmailTemplates"/>. <c>Email:Provider</c> picks SendGrid or the
/// console (<c>Log</c>, the default); the settings are checked at startup.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"), new EmailModule());
/// </code>
/// </example>
public sealed class EmailModule : IModule
{
    /// <summary>How long one SendGrid request may take before it's retried.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public string Name => "Email";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());
        services.RequireWhenDeployed(
            "Email:Provider",
            $"{EmailOptions.SendGridProvider} (the default, {EmailOptions.LogProvider}, sends nothing)",
            v => v == EmailOptions.SendGridProvider);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ProviderRetry(sp.GetRequiredService<TimeProvider>()));

        var provider = configuration[$"{EmailOptions.SectionName}:{nameof(EmailOptions.Provider)}"] ?? EmailOptions.LogProvider;
        if (provider == EmailOptions.SendGridProvider)
        {
            services.AddHttpClient<IEmailSender, SendGridEmailSender>(client => client.Timeout = RequestTimeout);
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
    }
}
