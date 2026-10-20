using System.Reflection;
using System.Text.Json.Serialization;
using BJK.Modules.Shared.Behaviours;
using BJK.Modules.Shared.Configuration;
using BJK.Modules.Shared.Errors;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Shared;

public static class ModuleServiceCollectionExtensions
{
    /// <summary>Configuration key for the MediatR licence key (a secret: user secrets locally, a container secret when deployed).</summary>
    public const string MediatRLicenseKeySetting = "MediatR:LicenseKey";

    /// <summary>
    /// Registers every module, then wires MediatR, FluentValidation and MVC controllers
    /// across all module assemblies in one place.
    /// </summary>
    /// <remarks>
    /// The host must register a bearer token handler under <see cref="ModuleAuthentication.Scheme"/>,
    /// then call <see cref="InitialiseBjkModulesAsync"/> after building the app.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"));
    /// var app = builder.Build();
    /// await app.Services.InitialiseBjkModulesAsync();
    /// app.UseExceptionHandler();
    /// app.MapControllers();
    /// </code>
    /// </example>
    public static IServiceCollection AddBjkModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (modules.Length == 0)
        {
            throw new ArgumentException("At least one module must be supplied.", nameof(modules));
        }

        Assembly[] assemblies = modules
            .Select(m => m.GetType().Assembly)
            .Distinct()
            .ToArray();

        // Enums travel as names ("Deactivated", "RoadRage"), not numbers, in every module API.
        var mvc = services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        foreach (var module in modules)
        {
            services.AddSingleton(module);
            module.AddServices(services, configuration);
        }

        foreach (var assembly in assemblies)
        {
            mvc.AddApplicationPart(assembly);
        }

        // The Posts module stores the moderation audit log; without it, entries go to the application log.
        services.TryAddScoped<IModerationAuditLog, LoggingModerationAuditLog>();

        // Shared endpoints, such as GET /api/me/data-export.
        mvc.AddApplicationPart(typeof(ModuleServiceCollectionExtensions).Assembly);
        services.AddAuthorizationBuilder()
            .AddPolicy(ModuleAuthentication.SignedInUserPolicy, policy => policy
                .AddAuthenticationSchemes(ModuleAuthentication.Scheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.GetIdentityId() is not null));

        // Deployed environments must have every setting (see DeployedSettings); the MediatR licence is common to all.
        services.RequireWhenDeployed(MediatRLicenseKeySetting, "the MediatR licence key");

        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSafetyOptions(configuration);
        services.AddPlans(configuration);

        // MediatR is registered once for all modules so pipeline behaviours aren't duplicated.
        services.AddMediatR(cfg =>
        {
            cfg.LicenseKey = configuration[MediatRLicenseKeySetting];
            cfg.RegisterServicesFromAssemblies(assemblies);
            // The age check comes first, so an unverified user isn't asked to fix fields they can't submit anyway.
            cfg.AddOpenBehavior(typeof(AgeVerificationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
        });

        services.AddValidatorsFromAssemblies(assemblies, includeInternalTypes: true);

        services.AddProblemDetails();
        services.AddExceptionHandler<ModuleExceptionHandler>();

        return services;
    }

    /// <summary>Runs each module's startup work, such as applying database migrations, in registration order.</summary>
    public static async Task InitialiseBjkModulesAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ModuleServiceCollectionExtensions));

        foreach (var module in services.GetServices<IModule>())
        {
            logger.LogInformation("Initialising module {Module}.", module.Name);
            await module.InitialiseAsync(services, cancellationToken);
        }
    }
}
