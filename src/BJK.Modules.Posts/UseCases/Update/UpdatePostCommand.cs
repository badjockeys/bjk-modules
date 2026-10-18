using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Update;

/// <summary>
/// Edits a post's details. Its photos and videos can't be changed. The author only. The text is screened again.
/// <c>IsSensitive</c>: true or false to change the sensitive tag, null to leave it.
/// </summary>
public sealed record UpdatePostCommand(
    Guid PostId,
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    bool? IsSensitive = null) : IRequest<PostDto>, IPostDetails, IRequiresAgeVerification;

public sealed class UpdatePostCommandValidator(TimeProvider clock) : PostDetailsValidator<UpdatePostCommand>(clock);

public sealed class UpdatePostCommandHandler(
    IPostRepository posts,
    IContentScreener screener,
    PostViews views,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<UpdatePostCommand, PostDto>
{
    public async Task<PostDto> Handle(UpdatePostCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);

        var now = clock.GetUtcNow();
        post.Update(request.ToDetails(), now);
        if (request.IsSensitive is { } sensitive)
        {
            post.SetSensitive(sensitive, now);
        }

        post.ApplyScreening(await screener.ScreenAsync(PostAccess.TextOf(request), cancellationToken));
        await posts.SaveChangesAsync(cancellationToken);

        return await views.BuildAsync(post, caller.UserId(), cancellationToken);
    }
}
