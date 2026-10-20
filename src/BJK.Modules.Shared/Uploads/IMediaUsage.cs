using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BJK.Modules.Shared.Uploads;

/// <summary>
/// Tells the Media module which uploads another module is using, so uploads nothing uses can be deleted 24 hours after
/// they were uploaded (owner, 21 Oct 2026: the app uploads as soon as a file is chosen, so a closed tab or a removed
/// photo leaves uploads behind). Posts answers for posts and drafts, Profiles for avatars. Each module answers only
/// from its own tables, so Media never reads theirs.
/// </summary>
public interface IMediaUsage
{
    /// <summary>What this module uses media for, for logs (e.g. "posts and drafts").</summary>
    string Name { get; }

    /// <summary>Which of these media ids are in use. Ids not returned aren't used by this module.</summary>
    /// <param name="mediaIds">Media ids from the Media module.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<IReadOnlySet<Guid>> InUseAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken);
}

public static class MediaUsageServiceCollectionExtensions
{
    /// <summary>Registers one module's answer to "which uploads are in use?" (several modules can register one each).</summary>
    public static IServiceCollection AddMediaUsage<TUsage>(this IServiceCollection services)
        where TUsage : class, IMediaUsage
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IMediaUsage, TUsage>());
        return services;
    }
}
