using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.UseCases.Delete;

/// <summary>
/// Deletes a post and its photos and videos. The post row is kept (marked Deleted) for audit;
/// the media files are removed. Author or admin.
/// </summary>
public sealed record DeletePostCommand(Guid PostId) : IRequest;

public sealed class DeletePostCommandHandler(
    IPostRepository posts,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock,
    ILogger<DeletePostCommandHandler> logger)
    : IRequestHandler<DeletePostCommand>
{
    public async Task Handle(DeletePostCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);
        var mediaIds = post.Media.Select(m => m.MediaId).ToList();

        // Take the post down first; removing the files is best effort and can be retried by clean-up.
        post.Delete(clock.GetUtcNow());
        await posts.SaveChangesAsync(cancellationToken);

        try
        {
            await media.DeleteAsync(mediaIds, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post {PostId} was deleted but its media {MediaIds} could not be removed.", post.Id, mediaIds);
        }
    }
}
