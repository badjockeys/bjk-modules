using System.Globalization;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.UseCases.Publishing;

/// <summary>
/// "Post now, publish when ready": checks a pending post's photos and video in the Media module and publishes the post
/// once every one is Ready, or marks it Failed (and emails the author) if any failed, was rejected or was deleted.
/// It always reads the media's current state rather than trusting an event, so it's safe to run any number of times,
/// in any order: called when the post is created and for every <c>media.ready</c> / <c>media.failed</c> event. It also
/// applies automatic sensitive tags: if the media worker's graphic content detection marked any of the post's media,
/// the post is tagged sensitive (source Automatic) before it's published, recorded in the audit log, and queued for a
/// moderator to confirm or undo. Once a post is published, its media's uploaded originals are deleted.
/// </summary>
public sealed class PostMediaPublisher(
    IPostRepository posts,
    IMediaDirectory media,
    ModerationNotifications notifications,
    TimeProvider clock,
    ILogger<PostMediaPublisher> logger,
    IModerationAuditLog audit,
    INotificationSender? notifier = null)
{
    /// <summary>
    /// Publishes or fails the post if its media allow; does nothing while any are still processing or if the post
    /// isn't pending. Saves the change. Throws <see cref="DbUpdateConcurrencyException"/> if the post changed meanwhile
    /// (the caller retries or leaves it to whoever changed it).
    /// </summary>
    /// <param name="post">A tracked post.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The post's media state afterwards.</returns>
    public async Task<PostMediaState> RefreshAsync(Post post, CancellationToken cancellationToken)
    {
        if (!post.IsMediaPending)
        {
            return post.MediaState;
        }

        var ids = post.Media.OrderBy(m => m.Position).Select(m => m.MediaId).ToList();
        var found = (await media.GetAsync(ids, cancellationToken)).ToDictionary(m => m.Id);

        string? failure = null;
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var item) || item.Status == MediaStatus.Deleted)
            {
                failure = "A photo or video in this post was deleted before it finished processing.";
                break;
            }

            if (item.Status == MediaStatus.Failed)
            {
                failure = string.IsNullOrWhiteSpace(item.FailureReason)
                    ? "A photo or video in this post couldn't be processed."
                    : item.FailureReason;
                break;
            }
        }

        var now = clock.GetUtcNow();
        if (failure is not null)
        {
            if (post.MarkMediaFailed(failure, now))
            {
                await posts.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Post {PostId} won't be published: a photo or video failed.", post.Id);
                await NotifyAuthorAsync(post, cancellationToken);
            }
        }
        else if (found.Values.All(m => m.IsReady))
        {
            // Tag before publishing, in the same save, so a flagged post is never shown unblurred.
            var tagged = TagIfDetected(post, found.Values);
            if (post.MarkMediaReady(now))
            {
                await posts.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Post {PostId} published: all its photos and videos are ready.", post.Id);
                if (tagged)
                {
                    await RecordAutomaticTagAsync(post, cancellationToken);
                }

                await ReleaseOriginalsAsync(post, cancellationToken);

                // Passengers tagged while it was processing are asked now that they can open it.
                var waiting = (await posts.GetPassengersAsync([post.Id], cancellationToken))
                    .Where(t => t.Status == PassengerTagStatus.Pending)
                    .Select(t => t.UserId)
                    .ToList();
                await Passengers.PassengerTagging.NotifyTaggedAsync(notifier, post, waiting, cancellationToken);
            }
        }

        return post.MediaState;
    }

    /// <summary>
    /// The post's media are all ready and it's published: the uploaded originals aren't needed any more, so the Media
    /// module deletes them. A failure is only logged: the Media module's 24-hour sweep deletes them anyway.
    /// </summary>
    /// <param name="post">The post.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task ReleaseOriginalsAsync(Post post, CancellationToken cancellationToken)
    {
        try
        {
            await media.ReleaseOriginalsAsync(post.Media.Select(m => m.MediaId).ToList(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't delete the originals of post {PostId}'s media now; the 24-hour sweep will.", post.Id);
        }
    }

    /// <summary>
    /// For a post that's already published (e.g. a moderator had a video processed again): tags it sensitive if
    /// detection now marks any of its media. Saves the change. Safe to repeat.
    /// </summary>
    /// <param name="post">A tracked post.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task TagPublishedIfDetectedAsync(Post post, CancellationToken cancellationToken)
    {
        if (post.Status == PostStatus.Deleted)
        {
            return;
        }

        var found = await media.GetAsync(post.Media.Select(m => m.MediaId).ToList(), cancellationToken);
        if (TagIfDetected(post, found))
        {
            await posts.SaveChangesAsync(cancellationToken);
            await RecordAutomaticTagAsync(post, cancellationToken);
        }
        else if (found.Any(m => m.IsAutoSensitive))
        {
            // Already tagged; the detection score is still worth keeping.
            await posts.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Tags the post sensitive automatically (not saved) if detection marked any of these media. Returns true if it was
    /// tagged now; false if nothing was flagged, it was already tagged (the score is still recorded) or a moderator undid
    /// an automatic tag on it before.
    /// </summary>
    /// <param name="post">The post.</param>
    /// <param name="items">Its media, from the Media module.</param>
    public bool TagIfDetected(Post post, IEnumerable<MediaSummary> items)
    {
        var flagged = items.Where(m => m.IsAutoSensitive).ToList();
        return flagged.Count > 0 && post.MarkSensitiveAutomatically(flagged.Max(m => m.SensitiveScore), clock.GetUtcNow());
    }

    /// <summary>
    /// After an automatic tag is saved: records it in the moderation audit log (actor "Automatic") and makes sure every
    /// photo or video in the post is marked sensitive (their files move to private storage).
    /// </summary>
    /// <param name="post">The post, tagged and saved.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public async Task RecordAutomaticTagAsync(Post post, CancellationToken cancellationToken)
    {
        logger.LogInformation("Post {PostId} was tagged sensitive automatically (score {Score}); queued for review.", post.Id, post.SensitiveScore);
        await audit.RecordAsync(
            new ModerationAuditEntry(
                PostsAudit.Module,
                ModerationActions.AutoTagSensitive,
                "Post",
                post.Id.ToString(),
                "Graphic content detected automatically; waiting for a moderator to confirm or undo.",
                "Sensitive=False",
                $"Sensitive=True; Score={post.SensitiveScore?.ToString("0.000", CultureInfo.InvariantCulture) ?? "none"}",
                ModerationActor.AutomaticRole),
            cancellationToken);
        await media.SetSensitiveAsync(post.Media.Select(m => m.MediaId).ToList(), isSensitive: true, cancellationToken);
    }

    private async Task NotifyAuthorAsync(Post post, CancellationToken cancellationToken)
    {
        if (post.NotifyEmail is null)
        {
            return;
        }

        // Sent once: the status change is saved first, and the address is forgotten afterwards either way.
        await notifications.PostNotPublishedAsync(post, cancellationToken);
        post.ClearNotifyEmail();
        try
        {
            await posts.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Couldn't clear the notification address of post {PostId}; it changed meanwhile.", post.Id);
        }
    }
}
