using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Infrastructure.Data;
using BJK.Modules.User.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.User;

/// <summary>
/// Registers the User module. It's hosted by bjk-identity-server, which must also register
/// an <see cref="IIdentityService"/> implementation and a token handler under <see cref="ModuleAuthentication.Scheme"/>.
/// </summary>
/// <param name="connectionStringName">Name of the connection string for the database holding the <see cref="Schema"/> tables.</param>
public sealed class UserModule(string connectionStringName) : IModule
{
    /// <summary>SQL schema for this module's tables. ("user" is reserved in SQL Server, hence "users".)</summary>
    public const string Schema = "users";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>Scope every caller of the users API needs in its access token.</summary>
    public const string ApiScope = "bjk.users";

    /// <summary>Scope for administrative access: list users and manage anyone's profile.</summary>
    public const string ManageScope = "bjk.users.manage";

    /// <summary>Authorization policy requiring <see cref="ManageScope"/>.</summary>
    public const string ManagePolicy = "bjk.users.manage";

    public string Name => "User";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' is not configured.");

        services.AddDbContext<UserDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable(MigrationsHistoryTable, Schema);
                sql.EnableRetryOnFailure();
            }));

        services.AddScoped<IUserRepository, UserRepository>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ManageScope)));
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<UserDbContext>(cancellationToken);
}
