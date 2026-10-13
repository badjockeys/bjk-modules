using BJK.Modules.Shared;
using BJK.Modules.User.Infrastructure.Data;
using BJK.Modules.User.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User;

/// <summary>Registers the User module. Load it from the host with <c>AddBjkModules(config, new UserModule())</c>.</summary>
public sealed class UserModule : IModule
{
    /// <summary>SQL schema for this module's tables. ("user" is reserved in SQL Server, hence "users".)</summary>
    public const string Schema = "users";

    /// <summary>Connection string name shared by all modules (the app's Azure SQL database).</summary>
    public const string ConnectionStringName = "AppDatabase";

    public string Name => "User";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<UserDbContext>(options => options.UseSqlServer(
            connectionString,
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);
                sql.EnableRetryOnFailure();
            }));

        services.AddScoped<IUserRepository, UserRepository>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<IdentityServerOptions>()
            .Bind(configuration.GetSection(IdentityServerOptions.SectionName))
            .Validate(o => o.Authority is not null, $"{IdentityServerOptions.SectionName}:Authority is required.")
            .ValidateOnStart();

        services.AddHttpClient<IIdentityService, IdentityServerClient>((sp, http) =>
            http.BaseAddress = sp.GetRequiredService<IOptions<IdentityServerOptions>>().Value.Authority);
    }
}
