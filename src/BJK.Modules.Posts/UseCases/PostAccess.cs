using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;

namespace BJK.Modules.Posts.UseCases;

internal static class PostAccess
{
    public static bool IsAdmin(ICurrentUser caller) => caller.HasScope(PostsModule.ManageScope);

    public static bool CanSee(Post post, ICurrentUser caller) =>
        post.IsPublic || IsAuthorOrAdmin(post, caller);

    public static bool IsAuthorOrAdmin(Post post, ICurrentUser caller) =>
        (caller.UserId() is { } userId && post.IsOwnedBy(userId)) || IsAdmin(caller);

    /// <summary>
    /// A post the caller can see. Hidden posts look missing to everyone but their author and admins,
    /// so nobody can probe for them.
    /// </summary>
    public static async Task<Post> GetVisibleAsync(
        IPostRepository posts,
        ICurrentUser caller,
        Guid postId,
        CancellationToken cancellationToken)
    {
        var post = await posts.GetAsync(postId, cancellationToken);
        return post is not null && CanSee(post, caller) ? post : throw new NotFoundException("Post", postId);
    }

    /// <summary>A post anyone can see, for reacting, commenting and reporting.</summary>
    public static async Task<Post> GetPublishedAsync(
        IPostRepository posts,
        Guid postId,
        CancellationToken cancellationToken)
    {
        var post = await posts.GetAsync(postId, cancellationToken);
        return post is { IsPublic: true } ? post : throw new NotFoundException("Post", postId);
    }

    /// <summary>A post the caller may change: their own, or anyone's for an admin.</summary>
    public static async Task<Post> GetForChangeAsync(
        IPostRepository posts,
        ICurrentUser caller,
        Guid postId,
        CancellationToken cancellationToken)
    {
        var post = await GetVisibleAsync(posts, caller, postId, cancellationToken);
        return IsAuthorOrAdmin(post, caller)
            ? post
            : throw new ForbiddenException("You can only change your own posts.");
    }

    public static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
