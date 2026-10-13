using System.Reflection;
using BJK.Modules.Shared.Behaviours;
using BJK.Modules.Shared.Errors;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.Shared;

public static class ModuleServiceCollectionExtensions
{
    /// <summary>Configuration key for the MediatR licence key (store the value in Key Vault).</summary>
    public const string MediatRLicenseKeySetting = "MediatR:LicenseKey";

    /// <summary>
    /// Registers every module, then wires MediatR, FluentValidation and MVC controllers
    /// across all module assemblies in one place.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddBjkModules(builder.Configuration, new UserModule());
    /// ...
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

        var mvc = services.AddControllers();
        foreach (var module in modules)
        {
            module.AddServices(services, configuration);
        }

        foreach (var assembly in assemblies)
        {
            mvc.AddApplicationPart(assembly);
        }

        // MediatR is registered once for all modules so pipeline behaviours aren't duplicated.
        services.AddMediatR(cfg =>
        {
            cfg.LicenseKey = configuration[MediatRLicenseKeySetting];
            cfg.RegisterServicesFromAssemblies(assemblies);
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
        });

        services.AddValidatorsFromAssemblies(assemblies, includeInternalTypes: true);

        services.AddProblemDetails();
        services.AddExceptionHandler<ModuleExceptionHandler>();

        return services;
    }
}
