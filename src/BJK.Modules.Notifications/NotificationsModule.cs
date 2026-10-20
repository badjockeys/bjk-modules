using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
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
/// emails that go with them. Other modules send through <see cref="INotificationSender"/>. Hosted by the bjk-core API
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
