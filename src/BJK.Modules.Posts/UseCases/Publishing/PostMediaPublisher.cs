using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Moderation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.UseCases.Publishing;

/// <summary>
/// "Post now, publish when ready": checks a pending post's photos and video in the Media module and publishes the post
/// once every one is Ready, or marks it Failed (and emails the author) if any failed, was rejected or was deleted.
/// It always reads the media's current state rather than trusting an event, so it's safe to run any number of times,
/// in any order: called when the post is created and for every <c>media.ready</c> / <c>media.failed</c> event.
/// </summary>
public sealed class PostMediaPublisher(
    IPostRepository posts,
    IMediaDirectory media,
    ModerationNotifications notifications,
    TimeProvider clock,
    ILogger<PostMediaPublisher> logger)
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
        else if (found.Values.All(m => m.IsReady) && post.MarkMediaReady(now))
        {
            await posts.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Post {PostId} published: all its photos and videos are ready.", post.Id);
        }

        return post.MediaState;
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
