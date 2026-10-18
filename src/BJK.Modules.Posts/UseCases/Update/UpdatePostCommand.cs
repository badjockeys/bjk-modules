using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Update;

/// <summary>Edits a post's details. Its photos and videos can't be changed. Author or admin.</summary>
public sealed record UpdatePostCommand(
    Guid PostId,
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName) : IRequest<PostDto>, IPostDetails;

public sealed class UpdatePostCommandValidator(TimeProvider clock) : PostDetailsValidator<UpdatePostCommand>(clock);

public sealed class UpdatePostCommandHandler(
    IPostRepository posts,
    PostViews views,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<UpdatePostCommand, PostDto>
{
    public async Task<PostDto> Handle(UpdatePostCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);

        post.Update(request.ToDetails(), clock.GetUtcNow());
        await posts.SaveChangesAsync(cancellationToken);

        return await views.BuildAsync(post, caller.UserId(), cancellationToken);
    }
}
