using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Reports;

/// <summary>Reports a published post for an admin to review. One report per user per post.</summary>
public sealed record ReportPostCommand(Guid PostId, ReportReason Reason, string? Details) : IRequest;

/// <summary>Reports a visible comment for an admin to review. One report per user per comment.</summary>
public sealed record ReportCommentCommand(Guid PostId, Guid CommentId, ReportReason Reason, string? Details) : IRequest;

public sealed class ReportPostCommandValidator : AbstractValidator<ReportPostCommand>
{
    public ReportPostCommandValidator()
    {
        RuleFor(c => c.Reason).IsInEnum();
        RuleFor(c => c.Details).ReportDetails();
    }
}

public sealed class ReportCommentCommandValidator : AbstractValidator<ReportCommentCommand>
{
    public ReportCommentCommandValidator()
    {
        RuleFor(c => c.Reason).IsInEnum();
        RuleFor(c => c.Details).ReportDetails();
    }
}

public sealed class ReportPostCommandHandler(
    IPostRepository posts,
    IReportRepository reports,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReportPostCommand>
{
    public async Task Handle(ReportPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);

        await ReportRules.EnsureFirstReportAsync(reports, userId, ReportTargetType.Post, post.Id, cancellationToken);

        reports.Add(Report.Create(userId, ReportTargetType.Post, post.Id, post.Id, request.Reason, request.Details, clock.GetUtcNow()));
        await reports.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReportCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IReportRepository reports,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReportCommentCommand>
{
    public async Task Handle(ReportCommentCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        var comment = await comments.GetAsync(post.Id, request.CommentId, cancellationToken);
        if (comment is not { Status: CommentStatus.Visible })
        {
            throw new NotFoundException("Comment", request.CommentId);
        }

        await ReportRules.EnsureFirstReportAsync(reports, userId, ReportTargetType.Comment, comment.Id, cancellationToken);

        reports.Add(Report.Create(userId, ReportTargetType.Comment, comment.Id, post.Id, request.Reason, request.Details, clock.GetUtcNow()));
        await reports.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Reports for moderators to review, oldest first. Moderators and admins.</summary>
public sealed record ListReportsQuery(ReportStatus Status = ReportStatus.Open, int Page = 1, int PageSize = 50)
    : IRequest<PagedResult<ReportDto>>;

public sealed class ListReportsQueryValidator : AbstractValidator<ListReportsQuery>
{
    public ListReportsQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListReportsQueryHandler(IReportRepository reports, ICurrentUser caller)
    : IRequestHandler<ListReportsQuery, PagedResult<ReportDto>>
{
    public async Task<PagedResult<ReportDto>> Handle(ListReportsQuery request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var (items, total) = await reports.ListAsync(request.Status, request.Page, request.PageSize, cancellationToken);
        return new PagedResult<ReportDto>(items.Select(ReportDto.From).ToList(), request.Page, request.PageSize, total);
    }
}

/// <summary>
/// A moderator's decision on a report. <see cref="ReportAction.HideContent"/> hides the post or comment and
/// closes every open report about it; <see cref="ReportAction.Dismiss"/> closes this report only.
/// </summary>
public sealed record ResolveReportCommand(Guid ReportId, ReportAction Action, string? Note) : IRequest<ReportDto>;

public sealed class ResolveReportCommandValidator : AbstractValidator<ResolveReportCommand>
{
    public ResolveReportCommandValidator()
    {
        RuleFor(c => c.Action).IsInEnum();
        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= Report.ResolutionNoteMaxLength)
            .WithMessage($"Notes are {Report.ResolutionNoteMaxLength} characters or fewer.");
    }
}

public sealed class ResolveReportCommandHandler(
    IReportRepository reports,
    IPostRepository posts,
    ICommentRepository comments,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ResolveReportCommand, ReportDto>
{
    public async Task<ReportDto> Handle(ResolveReportCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var now = clock.GetUtcNow();

        var report = await reports.GetAsync(request.ReportId, cancellationToken)
            ?? throw new NotFoundException("Report", request.ReportId);
        if (!report.IsOpen)
        {
            throw PostAccess.Invalid(nameof(request.ReportId), "This report has already been resolved.");
        }

        if (request.Action == ReportAction.HideContent)
        {
            var reason = string.IsNullOrWhiteSpace(request.Note) ? $"Reported: {report.Reason}" : request.Note.Trim();
            if (report.TargetType == ReportTargetType.Post)
            {
                var post = await posts.GetAsync(report.TargetId, cancellationToken);
                post?.Hide(reason, now);
            }
            else
            {
                var comment = await comments.GetAsync(report.PostId, report.TargetId, cancellationToken);
                comment?.Hide(reason, now);
            }

            foreach (var open in await reports.ListOpenForTargetAsync(report.TargetType, report.TargetId, cancellationToken))
            {
                if (open.Id != report.Id)
                {
                    open.Resolve(ReportAction.HideContent, caller.UserId(), request.Note, now);
                }
            }
        }

        report.Resolve(request.Action, caller.UserId(), request.Note, now);
        await reports.SaveChangesAsync(cancellationToken);

        return ReportDto.From(report);
    }
}

/// <summary>Makes a hidden post visible again. Moderators and admins.</summary>
public sealed record UnhidePostCommand(Guid PostId) : IRequest;

public sealed class UnhidePostCommandHandler(IPostRepository posts, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<UnhidePostCommand>
{
    public async Task Handle(UnhidePostCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var post = await posts.GetAsync(request.PostId, cancellationToken)
            ?? throw new NotFoundException("Post", request.PostId);

        post.Unhide(clock.GetUtcNow());
        await posts.SaveChangesAsync(cancellationToken);
    }
}

internal static class ReportRules
{
    public static void RequireModerator(ICurrentUser caller) => caller.RequireModerator(PostsModule.ManageScope);

    public static async Task EnsureFirstReportAsync(
        IReportRepository reports,
        Guid reporterId,
        ReportTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        if (await reports.ExistsAsync(reporterId, targetType, targetId, cancellationToken))
        {
            throw PostAccess.Invalid(string.Empty, "You've already reported this. Thanks, an admin will take a look.");
        }
    }

    public static IRuleBuilderOptions<T, string?> ReportDetails<T>(this IRuleBuilder<T, string?> rule) =>
        rule
            .Must(d => d is null || d.Trim().Length <= Report.DetailsMaxLength)
            .WithMessage($"Details are {Report.DetailsMaxLength} characters or fewer.");
}
