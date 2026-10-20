using System.Security.Cryptography;
using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.Infrastructure.Processing;

internal sealed partial class MediaProcessor
{
    /// <summary>What the uploader sees. Deliberately says nothing about why.</summary>
    internal const string BlockedReason = "This file can't be posted.";

    /// <summary>The original's MD5, for hash lists that use it. Null when processing a moderator's rework copy.</summary>
    private static async Task<string?> Md5Async(MediaItem item, string path, CancellationToken cancellationToken)
    {
        if (item.SourceKey is not null)
        {
            return null;
        }

        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await MD5.HashDataAsync(file, cancellationToken));
    }

    /// <summary>The original matches a known image's file hash; null if not (or the check is off).</summary>
    private KnownImageHit? MatchFile(MediaItem item, string sha256, string? md5) =>
        item.SourceKey is null ? knownImages?.MatchFile(sha256, md5) : null;

    /// <summary>The known-image match the redactor reported in its review reasons, if any.</summary>
    private async Task<KnownImageHit?> MatchFromReasonsAsync(IEnumerable<string> reasons, CancellationToken cancellationToken)
    {
        foreach (var reason in reasons)
        {
            if (KnownImageHit.FromReason(reason) is not { } found)
            {
                continue;
            }

            var row = knownImageRepository is null ? null : await knownImageRepository.GetHashAsync(found.HashId, cancellationToken);
            return row is null
                ? new KnownImageHit(found.HashId, KnownHashKind.Pdq, string.Empty, "(removed from the list)", null, found.Distance)
                : new KnownImageHit(row.Id, row.Kind, row.Value, row.Source, row.Reference, found.Distance);
        }

        return null;
    }

    /// <summary>The review reasons without the known-image marker.</summary>
    private static IEnumerable<string> WithoutKnownImage(IEnumerable<string> reasons) =>
        reasons.Where(r => !r.StartsWith(KnownImageHit.ReasonPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Blocks an upload that matched a known image: nothing is published, the original (and any rework copy) is
    /// deleted, the media is marked Failed with a generic message, and the match is recorded (no picture is kept)
    /// for moderators to review and report.
    /// </summary>
    private async Task<ProcessingOutcome> BlockAsync(MediaItem item, KnownImageHit hit, string? sha256, CancellationToken cancellationToken)
    {
        logger.LogCritical(
            "Media {MediaId} uploaded by {UserId} matched a known image ({Kind} from {Source}, distance {Distance}); it has been blocked and its files deleted.",
            item.Id,
            item.UserId,
            hit.Kind,
            hit.Source,
            hit.Distance);

        knownImageRepository?.Add(new KnownImageMatch(item.Id, item.UserId, hit, sha256 ?? item.Sha256, clock.GetUtcNow()));
        item.MarkFailed(BlockedReason, clock.GetUtcNow());
        try
        {
            await media.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The uploader deleted it meanwhile; the files go with it. Still record the match.
            logger.LogInformation("Media {MediaId} was deleted while it was being blocked.", item.Id);
            if (knownImageRepository is not null)
            {
                await knownImageRepository.SaveChangesAsync(CancellationToken.None);
            }
        }

        foreach (var key in new[] { item.OriginalKey, item.SourceKey }.OfType<string>().Distinct())
        {
            try
            {
                await storage.DeleteOriginalAsync(key, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't delete {Key} of blocked media {MediaId}; delete it by hand.", key, item.Id);
            }
        }

        return ProcessingOutcome.Failed;
    }
}
