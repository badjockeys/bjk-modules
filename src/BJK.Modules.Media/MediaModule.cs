using Amazon.S3;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media;

/// <summary>
/// Registers the Media module. Hosted by the bjk-core API, which must register a JWT bearer handler
/// under <see cref="ModuleAuthentication.Scheme"/> for tokens from bjk-identity-server.
/// </summary>
/// <param name="connectionStringName">Connection string for the app database holding the <see cref="Schema"/> tables.</param>
public sealed class MediaModule(string connectionStringName) : IModule
{
    public const string Schema = "media";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Scope the apps need to upload and view media.</summary>
    public const string ApiScope = "bjk.media";

    /// <summary>Admin scope: see and delete anyone's media.</summary>
    public const string ManageScope = "bjk.media.manage";

    /// <summary>Authorization policy requiring <see cref="ApiScope"/>.</summary>
    public const string UsePolicy = "bjk.media";

    public string Name => "Media";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<MediaDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IMediaDirectory, MediaDirectory>();

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaStorageOptions>()
            .Bind(configuration.GetSection(MediaStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<MediaQueueOptions>()
            .Bind(configuration.GetSection(MediaQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddBjkMessaging(configuration);

        services.AddSingleton<IAmazonS3>(sp =>
            S3MediaStorage.CreateClient(sp.GetRequiredService<IOptions<MediaStorageOptions>>().Value));
        services.AddSingleton<IMediaStorage, S3MediaStorage>();
        services.AddSingleton<IMediaJobQueue, RabbitMqMediaJobQueue>();

        services.AddScoped<IPersonalDataExporter, MediaPersonalDataExporter>();
        services.AddUserLifecycleHandler<MediaUserLifecycleHandler>(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ApiScope)));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<MediaDbContext>(cancellationToken);
}
