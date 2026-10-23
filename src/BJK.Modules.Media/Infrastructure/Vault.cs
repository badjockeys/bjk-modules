using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure;

/// <summary>
/// Which plan the vault follows for someone. Plans come with each request (the token, or <c>Plans:Overrides</c>), so
/// the plan seen is recorded (<see cref="VaultAccount"/>) for when nobody's signed in, e.g. a post published by a
/// background job. When it changes, every clip's expiry follows it (<see cref="VaultClip.FollowPlan"/>).
/// </summary>
public sealed class VaultPlans(IVaultStore store, IOptions<VaultOptions> options, IOptions<PlansOptions> plans, TimeProvider clock)
{
    /// <summary>How long a recorded plan is trusted before it's written again (the plan itself is always checked).</summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(12);

    /// <summary>Records the plan the signed-in person has now, and moves their clips' expiry if it changed.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="plan">Their plan, from this request.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<SubscriptionPlan> SeeAsync(Guid userId, SubscriptionPlan plan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var account = await store.GetAccountAsync(userId, cancellationToken);
        bool changed;
        if (account is null)
        {
            store.Add(VaultAccount.Create(userId, plan, now));
            changed = false;
        }
        else if (account.Plan == plan && now - account.PlanSeenAt < RefreshAfter)
        {
            return plan;
        }
        else
        {
            changed = account.See(plan, now);
        }

        if (changed)
        {
            var retention = options.Value.RetentionFor(plan);
            var notice = TimeSpan.FromDays(options.Value.DowngradeNoticeDays);
            foreach (var clip in await store.AllOfUserAsync(userId, cancellationToken))
            {
                clip.FollowPlan(retention, now, notice);
            }
        }

        await store.SaveChangesAsync(cancellationToken);
        return plan;
    }

    /// <summary>Someone's plan without a request: a <c>Plans:Overrides</c> entry, else the plan last seen, else Free.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<SubscriptionPlan> PlanOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in plans.Value.Overrides)
        {
            if (Guid.TryParse(key, out var overridden) && overridden == userId)
            {
                return SubscriptionPlanClaims.Parse(value);
            }
        }

        return (await store.GetAccountAsync(userId, cancellationToken))?.Plan ?? SubscriptionPlan.Free;
    }
}

/// <summary>
/// Keeps the originals of a published post's media in their owner's vault (owner, 23 Oct 2026) before the originals
/// are deleted: a copy inside the storage service, so even large videos take moments. Skipped when the vault is full
/// (the original is then deleted as before), or the media was itself posted from the vault.
/// </summary>
public sealed class VaultKeeper(
    IVaultStore store,
    IMediaStorage storage,
    VaultPlans plans,
    IOptions<VaultOptions> options,
    TimeProvider clock,
    ILogger<VaultKeeper> logger)
{
    /// <summary>Copies the originals into the vaults.</summary>
    /// <param name="items">Ready media whose originals are about to be deleted.</param>
    /// <param name="postId">The post just published, if known.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>Media whose copy failed: keep their originals for now (the 24-hour sweep deletes them later).</returns>
    public async Task<IReadOnlySet<Guid>> KeepAsync(IReadOnlyList<MediaItem> items, Guid? postId, CancellationToken cancellationToken)
    {
        var failed = new HashSet<Guid>();
        var candidates = items.Where(i => i.VaultClipId is null && i.UploadedAt is not null && i.OriginalDeletedAt is null).ToList();
        if (candidates.Count == 0)
        {
            return failed;
        }

        var already = await store.MediaInVaultAsync(candidates.Select(i => i.Id).ToList(), cancellationToken);
        var used = new Dictionary<Guid, long>();
        var added = 0;
        foreach (var item in candidates.Where(i => !already.Contains(i.Id)))
        {
            var plan = await plans.PlanOfAsync(item.UserId, cancellationToken);
            if (!used.TryGetValue(item.UserId, out var bytes))
            {
                bytes = await store.UsedBytesAsync(item.UserId, cancellationToken);
            }

            var size = item.OriginalSizeBytes ?? 0;
            if (bytes + size > options.Value.QuotaFor(plan))
            {
                logger.LogInformation("Not keeping media {MediaId}'s original: its owner's vault is full.", item.Id);
                continue;
            }

            var clip = VaultClip.FromPost(item, postId, clock.GetUtcNow(), options.Value.RetentionFor(plan));
            try
            {
                await storage.CopyInsideAsync(MediaBucket.Originals, item.OriginalKey, MediaBucket.Vault, clip.Key, cancellationToken);
                store.Add(clip);
                used[item.UserId] = bytes + size;
                added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't copy media {MediaId}'s original into the vault; keeping the original for now.", item.Id);
                failed.Add(item.Id);
            }
        }

        if (added > 0)
        {
            await store.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Kept {Count} original(s) in vaults.", added);
        }

        return failed;
    }
}

/// <summary>
/// The vault's part of the hourly sweep: deletes clips past their expiry, and uploads to the vault that were started
/// but never finished within <c>Media:UnusedOriginalRetention</c> (24 hours). File first, then the row.
/// </summary>
public sealed class VaultCleanup(
    IVaultStore store,
    IMediaStorage storage,
    IOptions<MediaOptions> options,
    TimeProvider clock,
    ILogger<VaultCleanup> logger)
{
    public const int SweepBatchSize = 200;

    /// <summary>One sweep. Returns how many clips were deleted.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await store.ListDueForDeletionAsync(now, now - options.Value.UnusedOriginalRetention, SweepBatchSize, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var clip in due)
        {
            await storage.DeleteAsync(MediaBucket.Vault, [clip.Key], cancellationToken);
            store.Remove(clip);
        }

        await store.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deleted {Count} vault clip(s) past their time, or never finished uploading.", due.Count);
        return due.Count;
    }
}
