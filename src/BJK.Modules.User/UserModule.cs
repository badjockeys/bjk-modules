using BJK.Modules.Shared;
using BJK.Modules.Shared.Data;
using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Users;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.Contracts;
using BJK.Modules.User.Infrastructure;
using BJK.Modules.User.Infrastructure.AgeAssurance;
using BJK.Modules.User.Infrastructure.Data;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Moderation scope, for the back-office: list accounts, deactivate and reactivate them (moderators and admins),
    /// delete them and set roles (admins). A user token also needs the role and two-factor sign-in.
    /// </summary>
    public const string ManageScope = "bjk.users.manage";

    /// <summary>Policy for the endpoints under <c>/api/admin/users</c>: moderator rights.</summary>
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
        services.AddScoped<AccountLifecycle>();
        services.AddScoped<IPersonalDataExporter, AccountPersonalDataExporter>();
        services.AddOptions<UserOptions>().Bind(configuration.GetSection(UserOptions.SectionName));

        AddAgeAssurance(services, configuration);

        // Account changes go out to bjk-core through the outbox (see AccountLifecycle).
        services.AddOutboxPublisher<UserDbContext>(configuration, UserLifecycleEvents.Exchange);
        services.AddHostedService<AccountRetentionService>();

        services.AddAuthorizationBuilder()
            .AddPolicy(ManagePolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasModeratorRights(ManageScope)));
    }

    /// <summary>
    /// The age check: settings (checked at startup), the development stand-ins and the generic HTTP estimator.
    /// The host adds its own providers (the identity server registers the YuNet liveness checker) as further
    /// <see cref="ILivenessChecker"/>, <see cref="IAgeEstimator"/> or <see cref="IDocumentAgeCheck"/> singletons;
    /// <c>AgeAssurance:*:Provider</c> picks one of each by name.
    /// </summary>
    private static void AddAgeAssurance(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgeAssuranceOptions>()
            .Bind(configuration.GetSection(AgeAssuranceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AgeAssuranceOptions>, AgeAssuranceOptionsValidator>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILivenessChecker, DevelopmentLivenessChecker>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgeEstimator, DevelopmentAgeEstimator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgeEstimator, HttpAgeEstimator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDocumentAgeCheck, DevelopmentDocumentAgeCheck>());
        services.AddHttpClient(HttpAgeEstimator.ClientName);
        services.AddSingleton<AgeAssuranceProviders>();
        services.AddScoped<IAgeVerificationLookup, AgeVerificationLookup>();
    }

    public Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateDatabaseAsync<UserDbContext>(cancellationToken);
}
