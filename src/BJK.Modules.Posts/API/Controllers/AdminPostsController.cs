using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Appeals;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Delete;
using BJK.Modules.Posts.UseCases.Get;
using BJK.Modules.Posts.UseCases.Hashtags;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Moderation, for the back-office. Needs the bjk.posts.manage scope and, for a user, the Moderator or Admin role
/// with two-factor sign-in. In production Cloudflare Access only lets the back-office reach /api/admin. Every action
/// needs a reason and is recorded in the moderation audit log.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix)]
[Authorize(Policy = PostsModule.ManagePolicy)]
public sealed class AdminPostsController(ISender sender) : ControllerBase
{
    /// <summary>Reports waiting for a decision (or in another status), urgent first, then by when they're due.</summary>
    [HttpGet("reports")]
    [ProducesResponseType<PagedResult<ReportDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<ReportDto>> ListReports(
        [FromQuery] ReportStatus status = ReportStatus.Open,
        [FromQuery] ReportPriority? priority = null,
        [FromQuery] ReportTargetType? targetType = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListReportsQuery(status, page, pageSize, priority, targetType), cancellationToken);

    /// <summary>
    /// Dismiss the report, or hide the content (which closes every open report about it). The note is required.
    /// Reporters are emailed the outcome.
    /// </summary>
    [HttpPost("reports/{id:guid}/resolve")]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ReportDto> ResolveReport(Guid id, ResolveReportRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ResolveReportCommand(id, request.Action, request.Note), cancellationToken);

    /// <summary>Appeals against moderation decisions, oldest due first.</summary>
    [HttpGet("appeals")]
    [ProducesResponseType<PagedResult<AppealDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<AppealDto>> ListAppeals(
        [FromQuery] AppealStatus status = AppealStatus.Open,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListAppealsQuery(status, page, pageSize), cancellationToken);

    /// <summary>Uphold the decision, or overturn it (the content is shown again). The note is required.</summary>
    [HttpPost("appeals/{id:guid}/decide")]
    [ProducesResponseType<AppealDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<AppealDto> DecideAppeal(Guid id, DecideAppealRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new DecideAppealCommand(id, request.Decision, request.Note), cancellationToken);

    /// <summary>Posts held by content screening (<c>queue=Held</c>) or published but flagged (<c>queue=Flagged</c>).</summary>
    [HttpGet("screening/posts")]
    [ProducesResponseType<PagedResult<ScreenedPostDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<ScreenedPostDto>> ListScreenedPosts(
        [FromQuery] ScreeningQueue queue = ScreeningQueue.Held,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListScreenedPostsQuery(queue, page, pageSize), cancellationToken);

    /// <summary>Approve (publish and clear the flag) or remove (hide) a screened post.</summary>
    [HttpPost("screening/posts/{id:guid}")]
    [ProducesResponseType<ScreenedPostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ScreenedPostDto> ReviewScreenedPost(Guid id, ScreeningReviewRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ReviewScreenedPostCommand(id, request.Decision, request.Reason), cancellationToken);

    /// <summary>Comments held by content screening (<c>queue=Held</c>) or published but flagged (<c>queue=Flagged</c>).</summary>
    [HttpGet("screening/comments")]
    [ProducesResponseType<PagedResult<ScreenedCommentDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<ScreenedCommentDto>> ListScreenedComments(
        [FromQuery] ScreeningQueue queue = ScreeningQueue.Held,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListScreenedCommentsQuery(queue, page, pageSize), cancellationToken);

    /// <summary>Approve (show and clear the flag) or remove (hide) a screened comment.</summary>
    [HttpPost("screening/comments/{commentId:guid}")]
    [ProducesResponseType<ScreenedCommentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ScreenedCommentDto> ReviewScreenedComment(
        Guid commentId,
        ScreeningReviewRequest request,
        CancellationToken cancellationToken) =>
        await sender.Send(new ReviewScreenedCommentCommand(commentId, request.Decision, request.Reason), cancellationToken);

    /// <summary>Any post, including hidden and held ones and those whose author is deactivated.</summary>
    [HttpGet("posts/{id:guid}")]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> GetPost(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetPostQuery(id, AsModerator: true), cancellationToken);

    /// <summary>Make a hidden post visible again. A reason is required.</summary>
    [HttpPost("posts/{id:guid}/unhide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnhidePost(Guid id, ModerationReasonRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new UnhidePostCommand(id, request.Reason), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Set or clear a post's sensitive tag (graphic content: its media are shown blurred except to age-verified adults).
    /// A reason is required. Also settles a pending review of an automatic tag.
    /// </summary>
    [HttpPut("posts/{id:guid}/sensitive")]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> SetSensitive(Guid id, SetSensitiveRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SetPostSensitiveCommand(id, request.IsSensitive, request.Reason), cancellationToken);

    /// <summary>Posts tagged sensitive by automatic detection, waiting for a moderator to confirm or undo the tag, oldest first.</summary>
    [HttpGet("posts/sensitive-review")]
    [ProducesResponseType<PagedResult<SensitiveReviewDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<SensitiveReviewDto>> ListSensitiveReview(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListSensitiveReviewQuery(page, pageSize), cancellationToken);

    /// <summary>Confirm (<c>confirm</c> = true) or undo an automatic sensitive tag. A reason is required.</summary>
    [HttpPost("posts/{id:guid}/sensitive-review")]
    [ProducesResponseType<SensitiveReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<SensitiveReviewDto> ReviewSensitiveTag(Guid id, SensitiveReviewRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ReviewSensitiveTagCommand(id, request.Confirm, request.Reason), cancellationToken);

    /// <summary>Delete anyone's post and its photos and videos. A reason is required (<c>?reason=</c>).</summary>
    [HttpDelete("posts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePost(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePostCommand(id, AsModerator: true, reason), cancellationToken);
        return NoContent();
    }

    /// <summary>Delete anyone's comment. A reason is required (<c>?reason=</c>).</summary>
    [HttpDelete("posts/{id:guid}/comments/{commentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComment(Guid id, Guid commentId, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCommentCommand(id, commentId, AsModerator: true, reason), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Hide a hashtag from Trending and from suggestions (<c>hidden: true</c>), or show it again. Posts keep the tag and
    /// its page still works. A reason is required.
    /// </summary>
    /// <param name="tag">The tag, with or without the # (any case).</param>
    /// <param name="request">Hidden or not, and why.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPut("hashtags/{tag}/trending")]
    [ProducesResponseType<HashtagDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<HashtagDto> SetHashtagHidden(string tag, HashtagTrendingRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SetHashtagHiddenCommand(tag, request.Hidden, request.Reason), cancellationToken);

    /// <summary>
    /// The moderation audit log for Posts, Profiles and Media, newest first. Admins only. Filter by actor, module,
    /// action, target and time (<c>from</c> inclusive, <c>to</c> exclusive).
    /// </summary>
    [HttpGet("audit")]
    [ProducesResponseType<PagedResult<AuditEntryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<PagedResult<AuditEntryDto>> ListAuditLog(
        [FromQuery] Guid? actorId = null,
        [FromQuery] string? module = null,
        [FromQuery(Name = "action")] string? auditAction = null,
        [FromQuery] string? targetType = null,
        [FromQuery] string? targetId = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListAuditLogQuery(actorId, module, auditAction, targetType, targetId, from, to, page, pageSize), cancellationToken);

    /// <summary>Blocked web addresses (posts and comments containing one are held for review); <c>?source=</c> filters.</summary>
    [HttpGet("blocked-urls")]
    [ProducesResponseType<PagedResult<BlockedUrlDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<BlockedUrlDto>> ListBlockedUrls(
        [FromQuery] string? source = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListBlockedUrlsQuery(source, page, pageSize), cancellationToken);

    /// <summary>Block a web address. A bare host covers the whole site.</summary>
    [HttpPost("blocked-urls")]
    [ProducesResponseType<BlockedUrlDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<BlockedUrlDto> AddBlockedUrl(AddBlockedUrlRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new AddBlockedUrlCommand(request.Address, request.Reason), cancellationToken);

    /// <summary>Unblock an address added by a moderator. A reason is required (<c>?reason=</c>).</summary>
    [HttpDelete("blocked-urls/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveBlockedUrl(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveBlockedUrlCommand(id, reason ?? string.Empty), cancellationToken);
        return NoContent();
    }
}

/// <summary>Hide a hashtag from Trending (or show it again), and why.</summary>
public sealed record HashtagTrendingRequest(bool Hidden, string? Reason);

/// <param name="Address">The address, e.g. example.com or https://example.com/page.</param>
/// <param name="Reason">Why, for the audit log.</param>
public sealed record AddBlockedUrlRequest(string Address, string Reason);
