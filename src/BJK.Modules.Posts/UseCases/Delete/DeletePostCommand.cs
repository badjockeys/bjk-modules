using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.UseCases.Delete;

/// <summary>
/// Deletes a post and its photos and videos. The post row is kept (marked Deleted) for audit;
/// the media files are removed. The author (public endpoint) or a moderator (admin endpoint, <c>AsModerator</c>, with
/// a <c>Reason</c>, recorded in the moderation audit log).
/// </summary>
public sealed record DeletePostCommand(Guid PostId, bool AsModerator = false, string? Reason = null) : IRequest;

public sealed class DeletePostCommandValidator : AbstractValidator<DeletePostCommand>
{
    public DeletePostCommandValidator()
    {
        When(c => c.AsModerator, () => RuleFor(c => c.Reason).ModerationReason());
    }
}

public sealed class DeletePostCommandHandler(
    IPostRepository posts,
    IMediaDirectory media,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock,
    ILogger<DeletePostCommandHandler> logger)
    : IRequestHandler<DeletePostCommand>
{
    public async Task Handle(DeletePostCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken, request.AsModerator);
        var mediaIds = post.Media.Select(m => m.MediaId).ToList();

        // Take the post down first; removing the files is best effort and can be retried by clean-up.
        var before = post.Status;
        post.Delete(clock.GetUtcNow());
        if (request.AsModerator)
        {
            await audit.RecordAsync(
                PostsAudit.Entry(ModerationActions.DeletePost, "Post", post.Id, request.Reason, $"Status={before}", $"Status={post.Status}"),
                cancellationToken);
        }

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
