using System.Text.RegularExpressions;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Reports;

/// <summary>Reports a published post for a moderator to review. One report per user per post.</summary>
public sealed record ReportPostCommand(Guid PostId, ReportReason Reason, string? Details) : IRequest, IRequiresAgeVerification;

/// <summary>Reports a visible comment for a moderator to review. One report per user per comment.</summary>
public sealed record ReportCommentCommand(Guid PostId, Guid CommentId, ReportReason Reason, string? Details)
    : IRequest, IRequiresAgeVerification;

/// <summary>Reports a user's profile (handle, name, bio, avatar) for a moderator to review. One report per user per profile.</summary>
public sealed record ReportProfileCommand(string Handle, ReportReason Reason, string? Details) : IRequest, IRequiresAgeVerification;

public sealed class ReportPostCommandValidator : AbstractValidator<ReportPostCommand>
{
    public ReportPostCommandValidator()
    {
        RuleFor(c => c.Reason).IsInEnum().NotForContent();
        RuleFor(c => c.Details).ReportDetails();
    }
}

public sealed class ReportCommentCommandValidator : AbstractValidator<ReportCommentCommand>
{
    public ReportCommentCommandValidator()
    {
        RuleFor(c => c.Reason).IsInEnum().NotForContent();
        RuleFor(c => c.Details).ReportDetails();
    }
}

public sealed class ReportProfileCommandValidator : AbstractValidator<ReportProfileCommand>
{
    public ReportProfileCommandValidator()
    {
        RuleFor(c => c.Handle).NotEmpty();
        RuleFor(c => c.Reason).IsInEnum();
        RuleFor(c => c.Details).ReportDetails();
    }
}

public sealed class ReportPostCommandHandler(
    IPostRepository posts,
    IReportRepository reports,
    ModerationNotifications notifications,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReportPostCommand>
{
    public async Task Handle(ReportPostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);

        await ReportRules.EnsureFirstReportAsync(reports, userId, ReportTargetType.Post, post.Id, cancellationToken);

        var report = Report.Create(userId, ReportTargetType.Post, post.Id, post.Id, request.Reason, request.Details, clock.GetUtcNow(), caller.Email);
        await ReportRules.SaveAndAcknowledgeAsync(reports, notifications, report, cancellationToken);
    }
}

public sealed class ReportCommentCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IReportRepository reports,
    ModerationNotifications notifications,
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

        var report = Report.Create(userId, ReportTargetType.Comment, comment.Id, post.Id, request.Reason, request.Details, clock.GetUtcNow(), caller.Email);
        await ReportRules.SaveAndAcknowledgeAsync(reports, notifications, report, cancellationToken);
    }
}

public sealed class ReportProfileCommandHandler(
    IProfileDirectory profiles,
    IReportRepository reports,
    ModerationNotifications notifications,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReportProfileCommand>
{
    public async Task Handle(ReportProfileCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var profile = await profiles.FindByHandleAsync(request.Handle, cancellationToken)
            ?? throw new NotFoundException("Profile", request.Handle);
        if (profile.UserId == userId)
        {
            throw PostAccess.Invalid(nameof(request.Handle), "You can't report your own profile.");
        }

        await ReportRules.EnsureFirstReportAsync(reports, userId, ReportTargetType.Profile, profile.UserId, cancellationToken);

        var report = Report.Create(userId, ReportTargetType.Profile, profile.UserId, null, request.Reason, request.Details, clock.GetUtcNow(), caller.Email);
        await ReportRules.SaveAndAcknowledgeAsync(reports, notifications, report, cancellationToken, profile.Handle);
    }
}

/// <summary>
/// The public "Report content / I'm in this clip" form, for anyone, with or without an account. <c>Target</c> is a
/// post id or a link to the post; <c>CommentId</c> optionally narrows it to one comment. Needs a CAPTCHA token
/// (Cloudflare Turnstile, in the <c>X-Captcha-Token</c> header), the reporter's name and email, and their consent
/// for us to use those details to handle the report. An acknowledgement email is sent straight away.
/// </summary>
public sealed record PublicReportCommand(
    string Target,
    Guid? CommentId,
    ReportReason Reason,
    string? Details,
    string ReporterName,
    string ReporterEmail,
    bool Consent,
    string? CaptchaToken,
    string? RemoteIp) : IRequest<ReportReceipt>;

public sealed class PublicReportCommandValidator : AbstractValidator<PublicReportCommand>
{
    /// <summary>The reasons offered on the public form.</summary>
    public static readonly IReadOnlySet<ReportReason> PublicReasons = new HashSet<ReportReason>
    {
        ReportReason.InThisClip,
        ReportReason.PersonalInformation,
        ReportReason.IllegalContent,
        ReportReason.Threat,
        ReportReason.HarassmentOrHate,
        ReportReason.Copyright,
        ReportReason.Other,
    };

    public PublicReportCommandValidator()
    {
        RuleFor(c => c.Target)
            .NotEmpty()
            .WithMessage("Paste the link to the post, or its id.")
            .MaximumLength(Report.TargetUrlMaxLength);
        RuleFor(c => c.Reason)
            .Must(PublicReasons.Contains)
            .WithMessage("Choose one of the reasons on the form.");
        RuleFor(c => c.Details).ReportDetails();
        RuleFor(c => c.ReporterName).NotEmpty().MaximumLength(Report.ReporterNameMaxLength);
        RuleFor(c => c.ReporterEmail).NotEmpty().EmailAddress().MaximumLength(Report.ContactEmailMaxLength);
        RuleFor(c => c.Consent)
            .Equal(true)
            .WithMessage("Please confirm we can use your name and email to handle your report.");
        RuleFor(c => c.CaptchaToken)
            .NotEmpty()
            .OverridePropertyName(CaptchaOptions.HeaderName)
            .WithMessage($"The {CaptchaOptions.HeaderName} header is required.");
    }
}

public sealed partial class PublicReportCommandHandler(
    IPostRepository posts,
    ICommentRepository comments,
    IReportRepository reports,
    ICaptchaVerifier captcha,
    ModerationNotifications notifications,
    TimeProvider clock)
    : IRequestHandler<PublicReportCommand, ReportReceipt>
{
    public async Task<ReportReceipt> Handle(PublicReportCommand request, CancellationToken cancellationToken)
    {
        switch (await captcha.VerifyAsync(request.CaptchaToken ?? string.Empty, request.RemoteIp, cancellationToken))
        {
            case CaptchaResult.Failed:
                throw PostAccess.Invalid(CaptchaOptions.HeaderName, "The CAPTCHA token is invalid or has expired. Get a new one and try again.");
            case CaptchaResult.Unavailable:
                throw new ServiceUnavailableException("The CAPTCHA couldn't be checked right now. Please try again in a moment.");
        }

        var postId = ParsePostId(request.Target)
            ?? throw PostAccess.Invalid(nameof(request.Target), "That doesn't look like a link to a Bad Jockeys post. Check the link and try again.");

        // Hidden and held posts can still be reported (people in them may have been told about them); deleted ones can't.
        var post = await posts.GetAsync(postId, cancellationToken)
            ?? throw PostAccess.Invalid(nameof(request.Target), "We couldn't find that post. It may already have been removed.");

        var targetType = ReportTargetType.Post;
        var targetId = post.Id;
        if (request.CommentId is { } commentId)
        {
            var comment = await comments.GetAsync(post.Id, commentId, cancellationToken)
                ?? throw PostAccess.Invalid(nameof(request.CommentId), "We couldn't find that comment on the post.");
            targetType = ReportTargetType.Comment;
            targetId = comment.Id;
        }

        var report = Report.CreatePublic(
            request.ReporterName,
            request.ReporterEmail,
            targetType,
            targetId,
            post.Id,
            request.Target,
            request.Reason,
            request.Details,
            clock.GetUtcNow());
        var sent = await ReportRules.SaveAndAcknowledgeAsync(reports, notifications, report, cancellationToken);
        return ReportReceipt.From(report, sent);
    }

    /// <summary>A post id on its own, or the first id in a link such as https://badjockeys.com/posts/{id}.</summary>
    internal static Guid? ParsePostId(string target)
    {
        var match = GuidPattern().Match(target ?? string.Empty);
        return match.Success && Guid.TryParse(match.Value, out var id) && id != Guid.Empty ? id : null;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();
}

/// <summary>Reports for moderators to review, urgent first, then by when a decision is due. Moderators and admins.</summary>
public sealed record ListReportsQuery(
    ReportStatus Status = ReportStatus.Open,
    int Page = 1,
    int PageSize = 50,
    ReportPriority? Priority = null,
    ReportTargetType? TargetType = null)
    : IRequest<PagedResult<ReportDto>>;

public sealed class ListReportsQueryValidator : AbstractValidator<ListReportsQuery>
{
    public ListReportsQueryValidator()
    {
        RuleFor(q => q.Status).IsInEnum();
        RuleFor(q => q.Priority).IsInEnum();
        RuleFor(q => q.TargetType).IsInEnum();
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
        var (items, total) = await reports.ListAsync(
            request.Status,
            request.Priority,
            request.TargetType,
            request.Page,
            request.PageSize,
            cancellationToken);
        return new PagedResult<ReportDto>(items.Select(ReportDto.From).ToList(), request.Page, request.PageSize, total);
    }
}

/// <summary>
/// A moderator's decision on a report. <see cref="ReportAction.HideContent"/> hides the post or comment (or resets a
/// profile's public details) and closes every open report about it; <see cref="ReportAction.Dismiss"/> closes this
/// report only. A note is required: it's the reason recorded in the moderation audit log. Each closed report's
/// reporter is emailed the outcome.
/// </summary>
public sealed record ResolveReportCommand(Guid ReportId, ReportAction Action, string? Note) : IRequest<ReportDto>;

public sealed class ResolveReportCommandValidator : AbstractValidator<ResolveReportCommand>
{
    public ResolveReportCommandValidator()
    {
        RuleFor(c => c.Action).IsInEnum();
        RuleFor(c => c.Note).ModerationReason();
    }
}

public sealed class ResolveReportCommandHandler(
    IReportRepository reports,
    IPostRepository posts,
    ICommentRepository comments,
    IProfileModeration profiles,
    IModerationAuditLog audit,
    ModerationNotifications notifications,
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

        var closed = new List<Report> { report };
        string? before = null;
        string? after = null;
        if (request.Action == ReportAction.HideContent)
        {
            var reason = string.IsNullOrWhiteSpace(request.Note) ? $"Reported: {report.Reason}" : request.Note.Trim();
            switch (report.TargetType)
            {
                case ReportTargetType.Post:
                    var post = await posts.GetAsync(report.TargetId, cancellationToken);
                    before = $"Post Status={post?.Status}";
                    post?.Hide(reason, now);
                    after = $"Post Status={post?.Status}";
                    break;
                case ReportTargetType.Comment:
                    var comment = report.PostId is { } postId
                        ? await comments.GetAsync(postId, report.TargetId, cancellationToken)
                        : await comments.FindAsync(report.TargetId, cancellationToken);
                    before = $"Comment Status={comment?.Status}";
                    comment?.Hide(reason, now);
                    after = $"Comment Status={comment?.Status}";
                    break;
                case ReportTargetType.Profile:
                    var reset = await profiles.ResetPublicDetailsAsync(report.TargetId, cancellationToken);
                    before = "Profile details=as written";
                    after = reset ? "Profile details=reset" : "Profile not found";
                    break;
            }

            foreach (var open in await reports.ListOpenForTargetAsync(report.TargetType, report.TargetId, cancellationToken))
            {
                if (open.Id != report.Id)
                {
                    open.Resolve(ReportAction.HideContent, caller.UserId(), request.Note, now);
                    closed.Add(open);
                }
            }
        }

        report.Resolve(request.Action, caller.UserId(), request.Note, now);
        await audit.RecordAsync(
            PostsAudit.Entry(
                ModerationActions.ResolveReport,
                "Report",
                report.Id,
                request.Note,
                before is null ? "Report=Open" : $"Report=Open; {before}",
                after is null ? $"Report={report.Status}" : $"Report={report.Status}; {after}; ReportsClosed={closed.Count}"),
            cancellationToken);
        await reports.SaveChangesAsync(cancellationToken);

        // Tell each reporter the outcome, then record who was told.
        var notified = false;
        foreach (var done in closed)
        {
            notified |= await notifications.ReportResolvedAsync(done, cancellationToken);
        }

        if (notified)
        {
            await reports.SaveChangesAsync(cancellationToken);
        }

        return ReportDto.From(report);
    }
}

/// <summary>Makes a hidden post visible again. Moderators and admins, with a reason (audited).</summary>
public sealed record UnhidePostCommand(Guid PostId, string? Reason) : IRequest;

public sealed class UnhidePostCommandValidator : AbstractValidator<UnhidePostCommand>
{
    public UnhidePostCommandValidator()
    {
        RuleFor(c => c.Reason).ModerationReason();
    }
}

public sealed class UnhidePostCommandHandler(IPostRepository posts, IModerationAuditLog audit, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<UnhidePostCommand>
{
    public async Task Handle(UnhidePostCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var post = await posts.GetAsync(request.PostId, cancellationToken)
            ?? throw new NotFoundException("Post", request.PostId);

        var before = post.Status;
        post.Unhide(clock.GetUtcNow());
        await audit.RecordAsync(
            PostsAudit.Entry(ModerationActions.UnhidePost, "Post", post.Id, request.Reason, $"Status={before}", $"Status={post.Status}"),
            cancellationToken);
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
            throw PostAccess.Invalid(string.Empty, "You've already reported this. Thanks, a moderator will take a look.");
        }
    }

    /// <summary>
    /// Saves a new report, emails it to the support inbox, emails the acknowledgement, and records that it was sent.
    /// True if the acknowledgement was sent.
    /// </summary>
    public static async Task<bool> SaveAndAcknowledgeAsync(
        IReportRepository reports,
        ModerationNotifications notifications,
        Report report,
        CancellationToken cancellationToken,
        string? profileHandle = null)
    {
        reports.Add(report);
        await reports.SaveChangesAsync(cancellationToken);
        await notifications.TellModeratorsAsync(report, profileHandle, cancellationToken);

        if (!await notifications.ReportReceivedAsync(report, cancellationToken))
        {
            return false;
        }

        await reports.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Impersonation and under 13 are about accounts, not posts or comments.</summary>
    public static IRuleBuilderOptions<T, ReportReason> NotForContent<T>(this IRuleBuilderOptions<T, ReportReason> rule) =>
        rule
            .Must(r => !ReportReasons.AccountOnly.Contains(r))
            .WithMessage("Impersonation and under 13 can only be reported on an account. Report the account instead.");

    public static IRuleBuilderOptions<T, string?> ReportDetails<T>(this IRuleBuilder<T, string?> rule) =>
        rule
            .Must(d => d is null || d.Trim().Length <= Report.DetailsMaxLength)
            .WithMessage($"Details are {Report.DetailsMaxLength} characters or fewer.");
}
