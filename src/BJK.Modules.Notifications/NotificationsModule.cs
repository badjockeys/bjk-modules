using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.Infrastructure.Push;
using BJK.Modules.Profiles;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Notifications;

/// <summary>
/// Registers the Notifications module: the bell (in-app notifications), each person's notification settings, and the
/// emails and web push notifications that go with them. Other modules send through <see cref="INotificationSender"/>. Hosted by the bjk-core API
/// with the Profiles module (for names) and the Email module. Signed-in users with the Profiles scope can use it.
/// </summary>
/// <param name="connectionStringName">Connection string for the app database holding the <see cref="Schema"/> tables.</param>
public sealed class NotificationsModule(string connectionStringName) : IModule
{
    public const string Schema = "notifications";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Policy for your own notifications: a signed-in user with the <c>bjk.profiles</c> scope.</summary>
    public const string UsePolicy = "bjk.notifications";

    public string Name => "Notifications";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<NotificationsDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddOptions<NotificationsOptions>().Bind(configuration.GetSection(NotificationsOptions.SectionName));
        services.RequireWhenDeployed("Notifications:AppUrl", "the web app's https address, for links in emails", DeployedSettingChecks.IsHttps);
        services.AddScoped<INotificationRepository, NotificationRepository>();

        // Replace the do-nothing sender and area alerts other modules register, whichever module is registered first.
        services.AddScoped<NotificationSender>();
        services.Replace(ServiceDescriptor.Scoped<INotificationSender>(sp => sp.GetRequiredService<NotificationSender>()));
        // Web push: off until VAPID keys are set; required live.
        services.AddHttpClient<IWebPushClient, WebPushClient>(http => http.Timeout = TimeSpan.FromSeconds(5));
        services.AddScoped<IPushSender, PushSender>();
        services.RequireWhenDeployed("Notifications:Push:PublicKey", "the VAPID public key (87 characters, base64url)", PushKeyChecks.IsPublicKey);
        services.RequireWhenDeployed("Notifications:Push:PrivateKey", "the VAPID private key (43 characters, base64url)", PushKeyChecks.IsPrivateKey);
        services.RequireWhenDeployed(
            "Notifications:Push:Subject",
            "mailto: an address push services can contact, or an https page",
            v => v is not null && (v.StartsWith("mailto:", StringComparison.Ordinal) || DeployedSettingChecks.IsHttps(v)),
            allowMissing: true);
        services.AddScoped<IAreaAlertRepository, AreaAlertRepository>();
        services.Replace(ServiceDescriptor.Scoped<IAreaAlerts, AreaAlerts>());
        services.Replace(ServiceDescriptor.Scoped<IHomeAreas, HomeAreas>());
        services.AddHostedService<AreaSummaries>();

        // Place search (home areas): postcodes.io, cached.
        services.AddMemoryCache();
        services.AddHttpClient<IPlaceLookup, PostcodesIoPlaceLookup>(http =>
        {
            http.BaseAddress = configuration.GetSection(NotificationsOptions.SectionName).Get<NotificationsOptions>()?.PlacesBaseUrl
                ?? new NotificationsOptions().PlacesBaseUrl;
            http.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddScoped<IPersonalDataExporter, NotificationsPersonalDataExporter>();
        services.AddUserLifecycleHandler<NotificationsUserLifecycleHandler>(configuration, connectionStringName);
        services.AddHostedService<NotificationsCleanup>();

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ProfilesModule.ApiScope)));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<NotificationsDbContext>(cancellationToken);
}
