using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Shared.Users;

/// <summary>Someone who has posted lately: how many public posts, and the reactions and comments they got.</summary>
public sealed record ActivePoster(Guid UserId, int Posts, int Engagement);

/// <summary>
/// Who posts where, for Who to follow (owner, 22 Oct 2026: "users who frequently post in my area"). Implemented by
/// the Posts module, where posts are stored; without it, nobody has posted.
/// </summary>
public interface IActivePosters
{
    /// <summary>
    /// The middle of where the user's own public posts since <paramref name="since"/> happened (to about 1 km), or
    /// null if they haven't posted.
    /// </summary>
    Task<(decimal Latitude, decimal Longitude)?> UsualPlaceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// The people with the most public posts since <paramref name="since"/> within <paramref name="km"/> of a point
    /// (or anywhere, if the point is null), most posts first, then most engagement; at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<ActivePoster>> MostActiveAsync(
        decimal? latitude,
        decimal? longitude,
        int km,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken);
}

internal sealed class NoActivePosters : IActivePosters
{
    public Task<(decimal Latitude, decimal Longitude)?> UsualPlaceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult<(decimal Latitude, decimal Longitude)?>(null);

    public Task<IReadOnlyList<ActivePoster>> MostActiveAsync(
        decimal? latitude,
        decimal? longitude,
        int km,
        DateTimeOffset since,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ActivePoster>>([]);
}

public static class ActivePostersServiceCollectionExtensions
{
    /// <summary>For modules that use posting activity: nobody has posted unless the Posts module registers the real one.</summary>
    public static IServiceCollection AddActivePostersFallback(this IServiceCollection services)
    {
        services.TryAddScoped<IActivePosters, NoActivePosters>();
        return services;
    }
}
