using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
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

    /// <summary>Logged-out viewers (and machine tokens): no comments, no sensitive posts.</summary>
    public static bool IsSignedOut(ICurrentUser caller) => caller.UserId() is null;

    /// <summary>
    /// Anyone sees public posts, except that logged-out viewers never see sensitive ones; authors also see their own
    /// hidden and held ones.
    /// </summary>
    public static bool CanSee(Post post, ICurrentUser caller) =>
        (post.IsPublic && !(post.IsSensitive && IsSignedOut(caller))) || IsAuthor(post, caller);

    /// <summary>A 404 if the viewer has blocked the post's author: blocked users' posts are hidden from them.</summary>
    public static async Task EnsureAuthorNotBlockedByViewerAsync(
        IBlockDirectory blocks,
        Post post,
        ICurrentUser caller,
        CancellationToken cancellationToken)
    {
        if (caller.UserId() is { } viewerId
            && viewerId != post.AuthorId
            && await blocks.IsBlockedAsync(viewerId, post.AuthorId, cancellationToken))
        {
            throw new NotFoundException("Post", post.Id);
        }
    }

    /// <summary>A 403 if the post's author has blocked the user: they can't comment on or react to the author's posts.</summary>
    public static async Task EnsureNotBlockedByAuthorAsync(
        IBlockDirectory blocks,
        Post post,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (userId != post.AuthorId && await blocks.IsBlockedAsync(post.AuthorId, userId, cancellationToken))
        {
            throw new ForbiddenException("You can't comment on or react to this post.");
        }
    }

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

    /// <summary>The text of a post that's screened and checked: title, description and place name.</summary>
    public static string TextOf(IPostDetails details) =>
        string.Join('\n', new[] { details.Title, details.Description, details.PlaceName }.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Moderation reasons: required, because every action is audited.</summary>
    public static IRuleBuilderOptions<T, string?> ModerationReason<T>(this IRuleBuilder<T, string?> rule) =>
        rule
            .NotEmpty()
            .WithMessage("Give a reason. It's recorded in the moderation audit log.")
            .Must(r => r is null || r.Trim().Length <= ModerationAuditRecord.ReasonMaxLength)
            .WithMessage($"Reasons are {ModerationAuditRecord.ReasonMaxLength} characters or fewer.");

    public static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
