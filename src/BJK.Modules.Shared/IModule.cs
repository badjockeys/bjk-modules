using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BJK.Modules.Shared;

/// <summary>
/// A feature module that a host (bjk-identity-server, bjk-core) loads at startup.
/// Each module registers its own services (DbContext, repositories, policies);
/// MediatR handlers, validators and controllers are discovered from the module's assembly
/// by <see cref="ModuleServiceCollectionExtensions.AddBjkModules"/>.
/// </summary>
public interface IModule
{
    /// <summary>Short module name, used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Registers the module's own services.</summary>
    void AddServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Runs once at startup, after the app is built and before it serves requests.
    /// Modules use it to bring their database schema up to date.
    /// </summary>
    Task InitialiseAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}
