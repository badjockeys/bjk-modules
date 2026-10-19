using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Publishing;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Publishes (or fails) the pending post that contains a photo or video when the Media module says it's ready or
/// failed. Idempotent: the post is re-checked against the media's current state, and a post that isn't pending any
/// more is left alone. If the post changed meanwhile, the event is retried.
/// </summary>
internal sealed class PostsMediaEventHandler(IPostRepository posts, PostMediaPublisher publisher) : IMediaEventHandler
{
    public async Task HandleAsync(MediaEvent mediaEvent, CancellationToken cancellationToken)
    {
        var post = await posts.FindByMediaAsync(mediaEvent.MediaId, cancellationToken);
        if (post is null || !post.IsMediaPending)
        {
            // Not in a post (an avatar, or the post isn't made yet: creating it checks the media itself), or done.
            return;
        }

        await publisher.RefreshAsync(post, cancellationToken);
    }
}
