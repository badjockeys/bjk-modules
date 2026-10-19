using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Appeals;

/// <summary>
/// Appeals a moderator's decision to hide the signed-in user's own post. Allowed once per post, and only while it's
/// hidden. The appeal goes to the moderators' queue and the user is emailed an acknowledgement.
/// </summary>
public sealed record AppealPostCommand(Guid PostId, string Statement) : IRequest<AppealDto>;

/// <summary>Appeals a moderator's decision to hide the signed-in user's own comment. Once per comment.</summary>
public sealed record AppealCommentCommand(Guid PostId, Guid CommentId, string Statement) : IRequest<AppealDto>;

public sealed class AppealPostCommandValidator : AbstractValidator<AppealPostCommand>
{
    public AppealPostCommandValidator()
    {
        RuleFor(c => c.Statement).AppealStatement();
    }
}

public sealed class AppealCommentCommandValidator : AbstractValidator<AppealCommentCommand>
{
    public AppealCommentCommandValidator()
    {
        RuleFor(c => c.Statement).AppealStatement();
    }
}

public sealed class AppealPostCommandHandler(
    IPostRepository posts,
    IAppealRepository appeals,
    ModerationNotifications notifications,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AppealPostCommand, AppealDto>
{
    public async Task<AppealDto> Handle(AppealPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);
        if (post.Status != PostStatus.Hidden)
        {
            throw PostAccess.Invalid(
                nameof(request.PostId),
                post.Status == PostStatus.HeldForReview
                    ? "Your post is already waiting for a moderator to review it."
                    : "Only a post a moderator has hidden can be appealed.");
        }

        var appeal = Appeal.Create(userId, ReportTargetType.Post, post.Id, post.Id, request.Statement, caller.Email, clock.GetUtcNow());
        return await AppealRules.SubmitAsync(appeals, notifications, appeal, cancellationToken);
    }
}

public sealed class AppealCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IAppealRepository appeals,
    ModerationNotifications notifications,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AppealCommentCommand, AppealDto>
{
    public async Task<AppealDto> Handle(AppealCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await posts.GetAsync(request.PostId, cancellationToken)
            ?? throw new NotFoundException("Post", request.PostId);
        var comment = await comments.GetAsync(post.Id, request.CommentId, cancellationToken);
        if (comment is null || !comment.IsOwnedBy(userId))
        {
            throw new NotFoundException("Comment", request.CommentId);
        }

        if (comment.Status != CommentStatus.Hidden)
        {
            throw PostAccess.Invalid(
                nameof(request.CommentId),
                comment.Status == CommentStatus.HeldForReview
                    ? "Your comment is already waiting for a moderator to review it."
                    : "Only a comment a moderator has hidden can be appealed.");
        }

        var appeal = Appeal.Create(userId, ReportTargetType.Comment, comment.Id, post.Id, request.Statement, caller.Email, clock.GetUtcNow());
        return await AppealRules.SubmitAsync(appeals, notifications, appeal, cancellationToken);
    }
}

/// <summary>Appeals for moderators to decide, oldest due first. Moderators and admins.</summary>
public sealed record ListAppealsQuery(AppealStatus Status = AppealStatus.Open, int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<AppealDto>>;

public sealed class ListAppealsQueryValidator : AbstractValidator<ListAppealsQuery>
{
    public ListAppealsQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListAppealsQueryHandler(IAppealRepository appeals, ICurrentUser caller)
    : IRequestHandler<ListAppealsQuery, PagedResult<AppealDto>>
{
    public async Task<PagedResult<AppealDto>> Handle(ListAppealsQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var (items, total) = await appeals.ListAsync(request.Status, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<AppealDto>(items.Select(AppealDto.From).ToList(), request.Page, request.PageSize, total);
    }
}

/// <summary>
/// A moderator's decision on an appeal. <see cref="AppealDecision.Overturn"/> makes the post or comment visible
/// again; <see cref="AppealDecision.Uphold"/> leaves it hidden. A note is required (audited); the user is emailed.
/// </summary>
public sealed record DecideAppealCommand(Guid AppealId, AppealDecision Decision, string? Note) : IRequest<AppealDto>;

public sealed class DecideAppealCommandValidator : AbstractValidator<DecideAppealCommand>
{
    public DecideAppealCommandValidator()
    {
        RuleFor(c => c.Decision).IsInEnum();
        RuleFor(c => c.Note).ModerationReason();
    }
}

public sealed class DecideAppealCommandHandler(
    IAppealRepository appeals,
    IPostRepository posts,
    ICommentRepository comments,
    IModerationAuditLog audit,
    ModerationNotifications notifications,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DecideAppealCommand, AppealDto>
{
    public async Task<AppealDto> Handle(DecideAppealCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(PostsModule.ManageScope);
        var now = clock.GetUtcNow();
        var appeal = await appeals.GetAsync(request.AppealId, cancellationToken)
            ?? throw new NotFoundException("Appeal", request.AppealId);
        if (!appeal.IsOpen)
        {
            throw PostAccess.Invalid(nameof(request.AppealId), "This appeal has already been decided.");
        }

        var note = (request.Note ?? string.Empty).Trim();
        string? contentAfter = null;
        if (request.Decision == AppealDecision.Overturn)
        {
            if (appeal.TargetType == ReportTargetType.Post)
            {
                var post = await posts.GetAsync(appeal.TargetId, cancellationToken);
                post?.Unhide(now);
                contentAfter = $"Post Status={post?.Status}";
            }
            else
            {
                var comment = await comments.GetAsync(appeal.PostId, appeal.TargetId, cancellationToken);
                comment?.Unhide(now);
                contentAfter = $"Comment Status={comment?.Status}";
            }
        }

        appeal.Decide(request.Decision, caller.UserId(), note, now);
        await audit.RecordAsync(
            PostsAudit.Entry(
                ModerationActions.DecideAppeal,
                "Appeal",
                appeal.Id,
                note,
                "Appeal=Open",
                contentAfter is null ? $"Appeal={appeal.Status}" : $"Appeal={appeal.Status}; {contentAfter}"),
            cancellationToken);
        await appeals.SaveChangesAsync(cancellationToken);

        await notifications.AppealDecidedAsync(appeal, cancellationToken);
        return AppealDto.From(appeal);
    }
}

internal static class AppealRules
{
    public static async Task<AppealDto> SubmitAsync(
        IAppealRepository appeals,
        ModerationNotifications notifications,
        Appeal appeal,
        CancellationToken cancellationToken)
    {
        if (await appeals.ExistsAsync(appeal.AppellantId, appeal.TargetType, appeal.TargetId, cancellationToken))
        {
            throw PostAccess.Invalid(string.Empty, "You've already appealed this decision. Each decision can be appealed once.");
        }

        appeals.Add(appeal);
        await appeals.SaveChangesAsync(cancellationToken);
        await notifications.AppealReceivedAsync(appeal, cancellationToken);
        return AppealDto.From(appeal);
    }

    public static IRuleBuilderOptions<T, string> AppealStatement<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .WithMessage("Tell us why you think the decision was wrong.")
            .Must(s => s is null || s.Trim().Length <= Appeal.StatementMaxLength)
            .WithMessage($"Appeals are {Appeal.StatementMaxLength} characters or fewer.");
}
