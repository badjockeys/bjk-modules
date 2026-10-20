using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Shared.Notifications;

/// <summary>
/// A post that has just become visible to everyone, for area alerts. <c>Latitude</c>/<c>Longitude</c> are the post's
/// stored (rounded) pin.
/// </summary>
public sealed record PublishedPost(
    Guid PostId,
    Guid AuthorId,
    string Title,
    string? PlaceName,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PublishedAt);

/// <summary>
/// Tells Premium members about new posts in their home area. Implemented by the Notifications module; without it,
/// nothing happens. Never throws: a failure is logged and the post stays published.
/// </summary>
public interface IAreaAlerts
{
    Task PostPublishedAsync(PublishedPost post, CancellationToken cancellationToken);
}

internal sealed class NoAreaAlerts : IAreaAlerts
{
    public Task PostPublishedAsync(PublishedPost post, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class AreaAlertServiceCollectionExtensions
{
    /// <summary>For modules that publish posts: a do-nothing <see cref="IAreaAlerts"/> unless the Notifications module registers the real one.</summary>
    public static IServiceCollection AddAreaAlertsFallback(this IServiceCollection services)
    {
        services.TryAddScoped<IAreaAlerts, NoAreaAlerts>();
        return services;
    }
}
