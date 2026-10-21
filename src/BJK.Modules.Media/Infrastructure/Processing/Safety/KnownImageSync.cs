using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Safety;

/// <summary>One hash from a list.</summary>
/// <param name="Kind">The kind of hash.</param>
/// <param name="Value">The hash, hex.</param>
/// <param name="Reference">The list's own id for it, if it has one.</param>
public sealed record KnownImageEntry(KnownHashKind Kind, string Value, string? Reference = null);

/// <summary>
/// A hash list to keep in SQL, such as the IWF Hash List (each provider gets its own implementation, written against
/// the provider's API once we're a member). <see cref="KnownImageSync"/> calls <see cref="FetchAsync"/> every
/// <see cref="KnownImageOptions.SyncInterval"/> and replaces that source's rows with what it returns. Register
/// implementations in the worker host; with none registered, only moderators' and test hashes are checked.
/// </summary>
public interface IKnownImageSource
{
    /// <summary>The source's name, stored with each hash (e.g. "IWF"). At most 50 characters.</summary>
    string Name { get; }

    /// <summary>The whole current list. Throw if it can't be fetched: the existing rows are kept.</summary>
    /// <param name="cancellationToken">Stops the fetch.</param>
    Task<IReadOnlyList<KnownImageEntry>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Keeps each <see cref="IKnownImageSource"/>'s hashes in <c>media.KnownImageHashes</c> up to date.</summary>
internal sealed class KnownImageSync(
    IEnumerable<IKnownImageSource> sources,
    IServiceScopeFactory scopes,
    IOptions<KnownImageOptions> options,
    TimeProvider clock,
    ILogger<KnownImageSync> logger)
    : BackgroundService
{
    private readonly IReadOnlyList<IKnownImageSource> _sources = sources.ToList();

    /// <summary>Replaces one source's rows with its current list: adds new hashes, removes ones no longer listed.</summary>
    /// <returns>How many were added and removed.</returns>
    public async Task<(int Added, int Removed)> SyncAsync(IKnownImageSource source, CancellationToken cancellationToken)
    {
        if (source.Name is KnownImageHash.ModerationSource or KnownImageHash.TestSource)
        {
            throw new InvalidOperationException($"\"{source.Name}\" is reserved.");
        }

        var fetched = await source.FetchAsync(cancellationToken);
        var wanted = new Dictionary<(KnownHashKind, string), KnownImageEntry>();
        var invalid = 0;
        foreach (var entry in fetched)
        {
            if (KnownImageHash.Normalise(entry.Kind, entry.Value) is { } value)
            {
                wanted.TryAdd((entry.Kind, value), entry);
            }
            else
            {
                invalid++;
            }
        }

        if (wanted.Count == 0 && fetched.Count > 0)
        {
            throw new InvalidOperationException($"Every hash from {source.Name} was invalid; the existing list is kept.");
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var existing = await db.KnownImageHashes.Where(h => h.Source == source.Name).ToListAsync(cancellationToken);
        var have = existing.Select(h => (h.Kind, h.Value)).ToHashSet();
        var removed = existing.Where(h => !wanted.ContainsKey((h.Kind, h.Value))).ToList();
        var now = clock.GetUtcNow();
        var added = wanted.Where(w => !have.Contains(w.Key))
            .Select(w => new KnownImageHash(w.Key.Item1, w.Key.Item2, source.Name, w.Value.Reference, now))
            .ToList();
        db.KnownImageHashes.RemoveRange(removed);
        db.KnownImageHashes.AddRange(added);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Synced the {Source} hash list: {Total} hashes, {Added} added, {Removed} removed, {Invalid} invalid skipped.",
            source.Name,
            wanted.Count,
            added.Count,
            removed.Count,
            invalid);
        return (added.Count, removed.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sources.Count == 0 || !options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.SyncInterval, clock);
        do
        {
            foreach (var source in _sources)
            {
                try
                {
                    await SyncAsync(source, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Critical: a list that stops updating misses new images. The previous list stays in use.
                    logger.LogCritical(ex, "Couldn't sync the {Source} hash list; the previous list stays in use.", source.Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
