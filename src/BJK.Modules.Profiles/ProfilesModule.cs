using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Profiles.Infrastructure;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.Profiles;

/// <summary>
/// Registers the Profiles module: each user's public profile. Hosted by the bjk-core API alongside the
/// Media module, which it uses for avatars. The host must register a JWT bearer handler under
/// <see cref="ModuleAuthentication.Scheme"/> for tokens from bjk-identity-server.
/// </summary>
/// <param name="connectionStringName">Connection string for the app database holding the <see cref="Schema"/> tables.</param>
public sealed class ProfilesModule(string connectionStringName) : IModule
{
    public const string Schema = "profiles";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Scope the apps need to create and change the signed-in user's profile.</summary>
    public const string ApiScope = "bjk.profiles";

    /// <summary>
    /// Moderation scope, for the back-office: look up any profile and reset offensive or identifying profiles.
    /// A user token also needs the Moderator or Admin role and two-factor sign-in.
    /// </summary>
    public const string ManageScope = "bjk.profiles.manage";

    /// <summary>Policy for the moderation endpoints under <c>/api/admin/profiles</c>.</summary>
    public const string ManagePolicy = "bjk.profiles.manage";

    /// <summary>Policy for changing your own profile: a signed-in user with <see cref="ApiScope"/>.</summary>
    public const string UsePolicy = "bjk.profiles";

    /// <summary>Policy for public reads. Anyone may call; a token, if sent, identifies the caller.</summary>
    public const string ReadPolicy = "bjk.profiles.read";

    public string Name => "Profiles";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<ProfilesDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));
        services.AddScoped<IProfileRepository, ProfileRepository>();
        services.AddScoped<IProfileDirectory, ProfileDirectory>();
        services.AddScoped<IBlockDirectory, BlockDirectory>();
        services.AddScoped<IProfileModeration, ProfileModeration>();
        services.AddScoped<IPersonalDataExporter, ProfilePersonalDataExporter>();
        services.AddUserLifecycleHandler<ProfilesUserLifecycleHandler>(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(UsePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ApiScope)))
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasModeratorRights(ManageScope)))
            .AddPolicy(ReadPolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAssertion(_ => true));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<ProfilesDbContext>(cancellationToken);
}
