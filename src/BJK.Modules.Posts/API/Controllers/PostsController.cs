using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Appeals;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Create;
using BJK.Modules.Posts.UseCases.Delete;
using BJK.Modules.Posts.UseCases.Get;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Passengers;
using BJK.Modules.Posts.UseCases.Reactions;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Posts.UseCases.Update;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

[ApiController]
[Route("api/posts")]
public sealed class PostsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// The public feed, newest first. Anyone can call this; logged-out callers never get sensitive posts, and
    /// signed-in callers don't get posts by people they've blocked.
    /// </summary>
    /// <param name="type">Only posts with this type of bad driving among their types.</param>
    /// <param name="category">The old name for <paramref name="type"/>, still accepted.</param>
    /// <param name="author">Only posts by this @handle.</param>
    /// <param name="lat">Centre of an area to search (with lng and radiusKm).</param>
    /// <param name="lng">Centre of an area to search (with lat and radiusKm).</param>
    /// <param name="radiusKm">Radius of the area, 1 to 200 km.</param>
    /// <param name="page">Page number, from 1.</param>
    /// <param name="pageSize">Posts per page, up to 50.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<PagedResult<PostDto>> List(
        [FromQuery] IncidentCategory? type,
        [FromQuery] IncidentCategory? category,
        [FromQuery] string? author,
        [FromQuery] decimal? lat,
        [FromQuery] decimal? lng,
        [FromQuery] int? radiusKm,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListPostsQuery(type ?? category, author, lat, lng, radiusKm, page, pageSize), cancellationToken);

    /// <summary>The signed-in user's posts, including hidden ones.</summary>
    [HttpGet("mine")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<PostDto>> ListMine(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListMyPostsQuery(page, pageSize), cancellationToken);

    /// <summary>
    /// Posts you're tagged in as a passenger, newest tag first: Pending (the default) are waiting for you to accept
    /// or decline; Accepted show your name.
    /// </summary>
    /// <param name="status">Pending, Accepted or Declined.</param>
    /// <param name="page">Page number, from 1.</param>
    /// <param name="pageSize">Posts per page, up to 50.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("tagged")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<PostDto>> ListTagged(
        [FromQuery] PassengerTagStatus status = PassengerTagStatus.Pending,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListTaggedPostsQuery(status, page, pageSize), cancellationToken);

    /// <summary>Accept (your name then shows on the post) or decline being tagged as a passenger.</summary>
    [HttpPut("{id:guid}/passengers/me")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> RespondToTag(Guid id, TagResponseRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new RespondToTagCommand(id, request.Accept), cancellationToken);

    /// <summary>Remove yourself from a post you were tagged on.</summary>
    [HttpDelete("{id:guid}/passengers/me")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMyTag(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveMyTagCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>One post. Anyone can call this; sensitive posts are 404 for logged-out callers.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetPostQuery(id), cancellationToken);

    /// <summary>Publish a post. Its photos and videos must be your own uploads and already Ready.</summary>
    [HttpPost]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreatePostCommand command, CancellationToken cancellationToken)
    {
        var post = await sender.Send(command, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = post.Id }, post);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> Update(Guid id, UpdatePostRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new UpdatePostCommand(
                id,
                request.Title,
                request.Description,
                request.Category,
                request.IncidentAt,
                request.Latitude,
                request.Longitude,
                request.PlaceName,
                request.IsSensitive,
                request.Types,
                request.Passengers),
            cancellationToken);

    /// <summary>Delete your post and its photos and videos.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePostCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Set your reaction to a post (replaces any earlier one).</summary>
    [HttpPut("{id:guid}/reaction")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> React(Guid id, ReactRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReactToPostCommand(id, request.Type), cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}/reaction")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveReaction(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveReactionCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>A post's comments, oldest first. Signed-in users only: logged-out viewers don't see comments.</summary>
    [HttpGet("{id:guid}/comments")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<CommentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PagedResult<CommentDto>> ListComments(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListCommentsQuery(id, page, pageSize), cancellationToken);

    [HttpPost("{id:guid}/comments")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<CommentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddComment(Guid id, CommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await sender.Send(new AddCommentCommand(id, request.Body), cancellationToken);
        return CreatedAtAction(nameof(ListComments), new { id }, comment);
    }

    /// <summary>
    /// One page of a comment's replies, oldest first (comment lists already include the first few). Signed-in users only.
    /// </summary>
    [HttpGet("{id:guid}/comments/{commentId:guid}/replies")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<CommentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PagedResult<CommentDto>> ListReplies(
        Guid id,
        Guid commentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListRepliesQuery(id, commentId, page, pageSize), cancellationToken);

    /// <summary>Reply to a top-level comment. Replies go one level deep: replying to a reply is a 400.</summary>
    [HttpPost("{id:guid}/comments/{commentId:guid}/replies")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<CommentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reply(Guid id, Guid commentId, CommentRequest request, CancellationToken cancellationToken)
    {
        var reply = await sender.Send(new AddCommentCommand(id, request.Body, commentId), cancellationToken);
        return CreatedAtAction(nameof(ListReplies), new { id, commentId }, reply);
    }

    /// <summary>Set your reaction to a comment or reply (replaces any earlier one).</summary>
    [HttpPut("{id:guid}/comments/{commentId:guid}/reaction")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReactToComment(Guid id, Guid commentId, ReactRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReactToCommentCommand(id, commentId, request.Type), cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id:guid}/comments/{commentId:guid}/reaction")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveCommentReaction(Guid id, Guid commentId, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveCommentReactionCommand(id, commentId), cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/comments/{commentId:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<CommentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<CommentDto> EditComment(
        Guid id,
        Guid commentId,
        CommentRequest request,
        CancellationToken cancellationToken) =>
        await sender.Send(new EditCommentCommand(id, commentId, request.Body), cancellationToken);

    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComment(Guid id, Guid commentId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCommentCommand(id, commentId), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/reports")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportPost(Guid id, ReportRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReportPostCommand(id, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }

    /// <summary>Appeal a moderator's decision to hide your post. Once per post.</summary>
    [HttpPost("{id:guid}/appeal")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<AppealDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<AppealDto> AppealPost(Guid id, AppealRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new AppealPostCommand(id, request.Statement), cancellationToken);

    /// <summary>Appeal a moderator's decision to hide your comment. Once per comment.</summary>
    [HttpPost("{id:guid}/comments/{commentId:guid}/appeal")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<AppealDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<AppealDto> AppealComment(Guid id, Guid commentId, AppealRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new AppealCommentCommand(id, commentId, request.Statement), cancellationToken);

    [HttpPost("{id:guid}/comments/{commentId:guid}/reports")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportComment(
        Guid id,
        Guid commentId,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        await sender.Send(new ReportCommentCommand(id, commentId, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// A post's new details. Send <c>Types</c> (the first is the main one), or one <c>Category</c>. <c>Passengers</c>
/// (@handles) replaces the tagged passengers; leave it out to keep them.
/// </summary>
public sealed record UpdatePostRequest(
    string Title,
    string? Description,
    IncidentCategory? Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    bool? IsSensitive = null,
    IReadOnlyList<IncidentCategory>? Types = null,
    IReadOnlyList<string>? Passengers = null);

/// <summary>Accept or decline being tagged as a passenger.</summary>
public sealed record TagResponseRequest(bool Accept);

public sealed record ReactRequest(ReactionType Type);

public sealed record CommentRequest(string Body);

public sealed record ReportRequest(ReportReason Reason, string? Details);

public sealed record ResolveReportRequest(ReportAction Action, string? Note);

public sealed record AppealRequest(string Statement);

/// <summary>Why a moderator took an action. Required: it's recorded in the moderation audit log.</summary>
public sealed record ModerationReasonRequest(string? Reason);

public sealed record SetSensitiveRequest(bool IsSensitive, string? Reason);

/// <summary>A moderator's decision on an automatic sensitive tag: keep it (<c>Confirm</c> = true) or undo it, and why.</summary>
public sealed record SensitiveReviewRequest(bool Confirm, string? Reason);

public sealed record ScreeningReviewRequest(ScreeningDecision Decision, string? Reason);

public sealed record DecideAppealRequest(AppealDecision Decision, string? Note);

/// <summary>The public "Report content / I'm in this clip" form.</summary>
public sealed record PublicReportRequest(
    string Target,
    Guid? CommentId,
    ReportReason Reason,
    string? Details,
    string ReporterName,
    string ReporterEmail,
    bool Consent);
