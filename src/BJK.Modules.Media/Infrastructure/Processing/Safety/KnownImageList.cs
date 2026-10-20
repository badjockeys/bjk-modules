using System.Numerics;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Processing.Safety;

/// <summary>
/// The known-image hashes (<c>media.KnownImageHashes</c>) held in memory by the media worker, so checking an upload
/// is a few milliseconds of arithmetic with no database or network call. Refreshed from SQL every
/// <see cref="KnownImageOptions.RefreshInterval"/> (only reloaded when the table has changed).
/// </summary>
public interface IKnownImageList
{
    /// <summary>True once the list has been loaded from the database at least once.</summary>
    bool IsLoaded { get; }

    /// <summary>How many hashes are loaded.</summary>
    int Count { get; }

    /// <summary>The closest PDQ entry within <see cref="KnownImageOptions.PdqMaxDistance"/> bits, or null.</summary>
    /// <param name="hash">A frame's hash.</param>
    KnownImageHit? MatchPdq(PdqHash hash);

    /// <summary>An entry with this exact file hash, or null.</summary>
    /// <param name="sha256">The file's SHA-256, hex.</param>
    /// <param name="md5">The file's MD5, hex.</param>
    KnownImageHit? MatchFile(string? sha256, string? md5);

    /// <summary>Reloads from the database if the table changed since the last load.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task RefreshAsync(CancellationToken cancellationToken);
}

internal sealed class KnownImageList(IServiceScopeFactory scopes, IOptions<KnownImageOptions> options, ILogger<KnownImageList> logger)
    : IKnownImageList
{
    private readonly KnownImageOptions _options = options.Value;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile Snapshot? _snapshot;

    public bool IsLoaded => _snapshot is not null;

    public int Count => _snapshot?.Count ?? 0;

    public KnownImageHit? MatchPdq(PdqHash hash)
    {
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.PdqWords.Length == 0)
        {
            return null;
        }

        var words = hash.Words;
        var (w0, w1, w2, w3) = (words[0], words[1], words[2], words[3]);
        var best = -1;
        var bestDistance = _options.PdqMaxDistance + 1;
        var list = snapshot.PdqWords;
        for (var i = 0; i < snapshot.PdqEntries.Length; i++)
        {
            var at = i * 4;
            var distance = BitOperations.PopCount(list[at] ^ w0) + BitOperations.PopCount(list[at + 1] ^ w1)
                + BitOperations.PopCount(list[at + 2] ^ w2) + BitOperations.PopCount(list[at + 3] ^ w3);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (i, distance);
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return best < 0 ? null : snapshot.PdqEntries[best].Hit(bestDistance);
    }

    public KnownImageHit? MatchFile(string? sha256, string? md5)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return null;
        }

        if (KnownImageHash.Normalise(KnownHashKind.Sha256, sha256) is { } sha && snapshot.Files.TryGetValue((KnownHashKind.Sha256, sha), out var a))
        {
            return a.Hit(null);
        }

        return KnownImageHash.Normalise(KnownHashKind.Md5, md5) is { } md && snapshot.Files.TryGetValue((KnownHashKind.Md5, md), out var b)
            ? b.Hit(null)
            : null;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            var version = await db.KnownImageHashes.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new ListVersion(g.Count(), g.Max(h => (DateTimeOffset?)h.AddedAt)))
                .FirstOrDefaultAsync(cancellationToken) ?? new ListVersion(0, null);
            if (_snapshot?.Version == version)
            {
                return;
            }

            var rows = await db.KnownImageHashes.AsNoTracking()
                .Select(h => new { h.Id, h.Kind, h.Value, h.Source, h.Reference })
                .ToListAsync(cancellationToken);
            var pdq = new List<Entry>();
            var words = new List<ulong>();
            var files = new Dictionary<(KnownHashKind, string), Entry>();
            foreach (var row in rows)
            {
                var entry = new Entry(row.Id, row.Kind, row.Value, row.Source, row.Reference);
                if (row.Kind == KnownHashKind.Pdq)
                {
                    if (PdqHash.FromHex(row.Value) is { } hash)
                    {
                        pdq.Add(entry);
                        words.AddRange(hash.Words.ToArray());
                    }
                }
                else
                {
                    files.TryAdd((row.Kind, row.Value), entry);
                }
            }

            _snapshot = new Snapshot(version, pdq.ToArray(), words.ToArray(), files);
            logger.LogInformation("Loaded {Count} known-image hashes ({Pdq} PDQ).", rows.Count, pdq.Count);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private sealed record ListVersion(int Count, DateTimeOffset? Latest);

    private sealed record Entry(Guid Id, KnownHashKind Kind, string Value, string Source, string? Reference)
    {
        public KnownImageHit Hit(int? distance) => new(Id, Kind, Value, Source, Reference, distance);
    }

    private sealed record Snapshot(ListVersion Version, Entry[] PdqEntries, ulong[] PdqWords, Dictionary<(KnownHashKind, string), Entry> Files)
    {
        public int Count => PdqEntries.Length + Files.Count;
    }
}

/// <summary>Loads the known-image list when the worker starts, then keeps it up to date.</summary>
internal sealed class KnownImageListRefresher(IKnownImageList list, IOptions<KnownImageOptions> options, ILogger<KnownImageListRefresher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.RefreshInterval);
        do
        {
            try
            {
                await list.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Until the first load works, uploads go to review (fail safe); after that the last list stays in use.
                logger.LogError(ex, "Couldn't refresh the known-image list; it will be tried again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
