using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.Shared;

/// <summary>
/// A feature module that the bjk-core host loads at startup.
/// Each module registers its own services (DbContext, repositories, clients);
/// MediatR handlers, validators and controllers are discovered from the module's assembly
/// by <see cref="ModuleServiceCollectionExtensions.AddBjkModules"/>.
/// </summary>
public interface IModule
{
    /// <summary>Short module name, used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Registers the module's own services.</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration);
}
