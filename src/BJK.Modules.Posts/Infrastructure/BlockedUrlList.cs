using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// A list of web addresses to keep in SQL, such as the IWF URL List (each provider gets its own implementation, written
/// against its API once we're a member). Synced every <see cref="BlockedUrlOptions.SyncInterval"/>: the source's rows
/// are replaced with what <see cref="FetchAsync"/> returns. With none registered, only moderators' and test entries apply.
/// </summary>
public interface IBlockedUrlSource
{
    /// <summary>The source's name, stored with each entry (e.g. "IWF"). At most 50 characters.</summary>
    string Name { get; }

    /// <summary>The whole current list of addresses. Throw if it can't be fetched: the existing rows are kept.</summary>
    Task<IReadOnlyList<string>> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Blocked-address settings. Section <c>BlockedUrls</c>.</summary>
public sealed class BlockedUrlOptions
{
    public const string SectionName = "BlockedUrls";

    /// <summary>How often the API checks SQL for changes to the list.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often each <see cref="IBlockedUrlSource"/> is synced into SQL.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>The blocked addresses held in memory in the API, so screening text costs no database call.</summary>
internal sealed class BlockedUrlList : IBlockedUrlList
{
    private volatile Snapshot _snapshot = new(null, [], new HashSet<string>(StringComparer.Ordinal));

    public bool IsLoaded => _snapshot.Version is not null;

    public int Count => _snapshot.Entries.Length;

    public string? FindIn(string text)
    {
        var snapshot = _snapshot;
        if (snapshot.Entries.Length == 0)
        {
            return null;
        }

        foreach (var url in BlockedUrls.Find(text))
        {
            if (snapshot.Exact.Contains(url))
            {
                return url;
            }

            // Hosts and path prefixes: check each shorter form of the address against the set.
            var host = url.Split('/', 2)[0];
            var parts = host.Split('.');
            for (var i = 1; i < parts.Length - 1; i++)
            {
                if (snapshot.Exact.Contains(string.Join('.', parts[i..])))
                {
                    return url;
                }
            }

            var cut = url.LastIndexOf('/');
            while (cut > 0)
            {
                var prefix = url[..cut];
                if (snapshot.Exact.Contains(prefix))
                {
                    return url;
                }

                cut = prefix.LastIndexOf('/');
            }
        }

        return null;
    }

    /// <summary>Replaces the list if the table changed.</summary>
    public async Task RefreshAsync(PostsDbContext db, CancellationToken cancellationToken)
    {
        var version = await db.BlockedUrls.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new ListVersion(g.Count(), g.Max(b => (DateTimeOffset?)b.AddedAt)))
            .FirstOrDefaultAsync(cancellationToken) ?? new ListVersion(0, null);
        if (_snapshot.Version == version)
        {
            return;
        }

        var patterns = await db.BlockedUrls.AsNoTracking().Select(b => b.Pattern).Distinct().ToArrayAsync(cancellationToken);
        Load(patterns, version);
    }

    /// <summary>Replaces the list with these (normalised) patterns.</summary>
    internal void Load(IReadOnlyCollection<string> patterns, ListVersion? version = null) =>
        _snapshot = new Snapshot(version ?? new ListVersion(patterns.Count, null), [.. patterns], new HashSet<string>(patterns, StringComparer.Ordinal));

    internal sealed record ListVersion(int Count, DateTimeOffset? Latest);

    private sealed record Snapshot(ListVersion? Version, string[] Entries, HashSet<string> Exact);
}

/// <summary>Syncs each <see cref="IBlockedUrlSource"/> into SQL and keeps the in-memory list up to date.</summary>
internal sealed class BlockedUrlMaintenance(
    BlockedUrlList list,
    IEnumerable<IBlockedUrlSource> sources,
    IServiceScopeFactory scopes,
    IOptions<BlockedUrlOptions> options,
    TimeProvider clock,
    ILogger<BlockedUrlMaintenance> logger)
    : BackgroundService
{
    private readonly IReadOnlyList<IBlockedUrlSource> _sources = sources.ToList();
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;

    /// <summary>Replaces one source's rows with its current list.</summary>
    public async Task<(int Added, int Removed)> SyncAsync(IBlockedUrlSource source, CancellationToken cancellationToken)
    {
        if (source.Name is BlockedUrl.ModerationSource or BlockedUrl.TestSource)
        {
            throw new InvalidOperationException($"\"{source.Name}\" is reserved.");
        }

        var fetched = await source.FetchAsync(cancellationToken);
        var wanted = fetched.Select(BlockedUrls.Normalise).OfType<string>()
            .Where(p => p.Length <= BlockedUrl.PatternMaxLength).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 && fetched.Count > 0)
        {
            throw new InvalidOperationException($"Every address from {source.Name} was invalid; the existing list is kept.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PostsDbContext>();
        var existing = await db.BlockedUrls.Where(b => b.Source == source.Name).ToListAsync(cancellationToken);
        var have = existing.Select(b => b.Pattern).ToHashSet(StringComparer.Ordinal);
        var removed = existing.Where(b => !wanted.Contains(b.Pattern)).ToList();
        var now = clock.GetUtcNow();
        var added = wanted.Where(p => !have.Contains(p)).Select(p => new BlockedUrl(p, source.Name, null, now)).ToList();
        db.BlockedUrls.RemoveRange(removed);
        db.BlockedUrls.AddRange(added);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Synced the {Source} address list: {Total} addresses, {Added} added, {Removed} removed.", source.Name, wanted.Count, added.Count, removed.Count);
        return (added.Count, removed.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RefreshInterval, clock);
        do
        {
            if (_sources.Count > 0 && clock.GetUtcNow() - _lastSync >= options.Value.SyncInterval)
            {
                _lastSync = clock.GetUtcNow();
                foreach (var source in _sources)
                {
                    try
                    {
                        await SyncAsync(source, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogCritical(ex, "Couldn't sync the {Source} address list; the previous list stays in use.", source.Name);
                    }
                }
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await list.RefreshAsync(scope.ServiceProvider.GetRequiredService<PostsDbContext>(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't refresh the blocked address list; it will be tried again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
