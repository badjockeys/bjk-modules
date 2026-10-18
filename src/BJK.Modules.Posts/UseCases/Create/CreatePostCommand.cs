using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Create;

/// <summary>
/// Publishes a post. Upload the photos and videos through <c>/api/media</c> first and wait until they're Ready;
/// the post is then visible to everyone straight away. The author needs a profile.
/// </summary>
public sealed record CreatePostCommand(
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    IReadOnlyList<Guid> MediaIds) : IRequest<PostDto>, IPostDetails;

public sealed class CreatePostCommandValidator : PostDetailsValidator<CreatePostCommand>
{
    public CreatePostCommandValidator(TimeProvider clock)
        : base(clock)
    {
        RuleFor(c => c.MediaIds)
            .NotEmpty()
            .WithMessage("Add at least one photo or video.")
            .Must(ids => ids is null || ids.Count <= Post.MaxMedia)
            .WithMessage($"A post can have up to {Post.MaxMedia} photos or videos.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Each photo or video can only be added once.")
            .Must(ids => ids is null || !ids.Contains(Guid.Empty))
            .WithMessage("Media ids can't be empty.");
    }
}

public sealed class CreatePostCommandHandler(
    IPostRepository posts,
    IProfileDirectory profiles,
    IMediaDirectory media,
    PostViews views,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CreatePostCommand, PostDto>
{
    public async Task<PostDto> Handle(CreatePostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "posting");

        if (!await profiles.ExistsAsync(userId, cancellationToken))
        {
            throw PostAccess.Invalid("Profile", "Create your profile before posting.");
        }

        var found = (await media.GetAsync(request.MediaIds, cancellationToken)).ToDictionary(m => m.Id);
        foreach (var id in request.MediaIds)
        {
            if (!found.TryGetValue(id, out var item) || item.UserId != userId)
            {
                throw PostAccess.Invalid(nameof(request.MediaIds), $"Media {id} isn't one of your uploads.");
            }

            if (!item.IsReady)
            {
                throw PostAccess.Invalid(
                    nameof(request.MediaIds),
                    $"Media {id} is {item.Status}. Wait until it has finished processing.");
            }
        }

        if (await posts.AnyUseMediaAsync(request.MediaIds, cancellationToken))
        {
            throw PostAccess.Invalid(nameof(request.MediaIds), "A photo or video is already in another post.");
        }

        var post = Post.Create(userId, request.ToDetails(), request.MediaIds, clock.GetUtcNow());
        posts.Add(post);
        await posts.SaveChangesAsync(cancellationToken);

        return await views.BuildAsync(post, userId, cancellationToken);
    }
}
