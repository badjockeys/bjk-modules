using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Shared.Notifications;

/// <summary>A user's home area: the place they chose, kept to about 1 km, and the radius they picked.</summary>
public sealed record HomeArea(string PlaceName, decimal Latitude, decimal Longitude, int RadiusKm);

/// <summary>
/// Users' home areas, for ranking (For you) and suggestions (Who to follow). Implemented by the Notifications module,
/// where home areas are stored; without it, nobody has one. The area's position is never shown to anyone else.
/// </summary>
public interface IHomeAreas
{
    /// <summary>The user's home area, or null if they haven't chosen one.</summary>
    Task<HomeArea?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>People whose home area is within <paramref name="km"/> of a point, nearest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> UsersNearAsync(decimal latitude, decimal longitude, int km, int limit, CancellationToken cancellationToken);
}

internal sealed class NoHomeAreas : IHomeAreas
{
    public Task<HomeArea?> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<HomeArea?>(null);

    public Task<IReadOnlyList<Guid>> UsersNearAsync(decimal latitude, decimal longitude, int km, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

public static class HomeAreaServiceCollectionExtensions
{
    /// <summary>For modules that use home areas: nobody has one unless the Notifications module registers the real store.</summary>
    public static IServiceCollection AddHomeAreasFallback(this IServiceCollection services)
    {
        services.TryAddScoped<IHomeAreas, NoHomeAreas>();
        return services;
    }
}
