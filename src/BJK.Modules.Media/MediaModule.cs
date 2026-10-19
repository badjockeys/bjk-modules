using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    /// <summary>
    /// Moderation scope, for the back-office: see and delete anyone's media. A user token also needs the Moderator or
    /// Admin role and two-factor sign-in.
    /// </summary>
    public const string ManageScope = "bjk.media.manage";

    /// <summary>Policy for the moderation endpoints under <c>/api/admin/media</c>.</summary>
    public const string ManagePolicy = "bjk.media.manage";

    /// <summary>Authorization policy requiring <see cref="ApiScope"/>.</summary>
    public const string UsePolicy = "bjk.media";

    public string Name => "Media";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        MediaServices.AddStorageAndData(services, configuration, connectionStringName);
        services.AddScoped<IMediaDirectory, MediaDirectory>();

        // Originals are deleted once their post is published; the sweeper catches the rest after 24 hours.
        services.AddScoped<OriginalsCleanup>();
        services.AddHostedService<OriginalsSweeper>();
        services.TryAddSingleton<IMediaJobQueue, MediaJobQueue>();

        // media.ready / media.failed: written to media.Outbox by the API and the workers, published from here.
        services.AddOutboxPublisher<MediaDbContext>(configuration, MediaEvents.Topic, connectionStringName);

        services.AddScoped<IPersonalDataExporter, MediaPersonalDataExporter>();
        services.AddUserLifecycleHandler<MediaUserLifecycleHandler>(configuration, connectionStringName);

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ApiScope)))
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasModeratorRights(ManageScope)));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<MediaDbContext>(cancellationToken);
}
