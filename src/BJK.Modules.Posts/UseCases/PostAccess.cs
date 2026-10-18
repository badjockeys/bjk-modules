using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;

namespace BJK.Modules.Posts.UseCases;

internal static class PostAccess
{
    /// <summary>Moderator rights in the Posts module (role + two-factor sign-in + the manage scope).</summary>
    public static bool IsModerator(ICurrentUser caller) => caller.HasModeratorRights(PostsModule.ManageScope);

    public static bool IsAuthor(Post post, ICurrentUser caller) =>
        caller.UserId() is { } userId && post.IsOwnedBy(userId);

    /// <summary>Anyone sees public posts; authors also see their own hidden ones.</summary>
    public static bool CanSee(Post post, ICurrentUser caller) => post.IsPublic || IsAuthor(post, caller);

    /// <summary>
    /// A post the caller can see. Hidden posts look missing to everyone but their author, so nobody can probe for
    /// them. Moderators see every post through the admin endpoints (<paramref name="asModerator"/>).
    /// </summary>
    public static async Task<Post> GetVisibleAsync(
        IPostRepository posts,
        ICurrentUser caller,
        Guid postId,
        CancellationToken cancellationToken,
        bool asModerator = false)
    {
        if (asModerator)
        {
            caller.RequireModerator(PostsModule.ManageScope);
        }

        var post = await posts.GetAsync(postId, cancellationToken);
        return post is not null && (asModerator || CanSee(post, caller)) ? post : throw new NotFoundException("Post", postId);
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

    /// <summary>
    /// A post the caller may change: their own on the public endpoints, or anyone's for a moderator on the admin
    /// endpoints (<paramref name="asModerator"/>).
    /// </summary>
    public static async Task<Post> GetForChangeAsync(
        IPostRepository posts,
        ICurrentUser caller,
        Guid postId,
        CancellationToken cancellationToken,
        bool asModerator = false)
    {
        var post = await GetVisibleAsync(posts, caller, postId, cancellationToken, asModerator);
        return asModerator || IsAuthor(post, caller)
            ? post
            : throw new ForbiddenException("You can only change your own posts.");
    }

    /// <summary>Posting and commenting need a confirmed email address, to keep throwaway spam accounts out.</summary>
    public static void RequireVerifiedEmail(ICurrentUser caller, string action)
    {
        if (!caller.EmailVerified)
        {
            throw Invalid("Email", $"Confirm your email address before {action}.");
        }
    }

    public static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
