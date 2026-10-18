using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Delete;
using BJK.Modules.Posts.UseCases.Get;
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
/// with two-factor sign-in. In production Cloudflare Access only lets the back-office reach /api/admin.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix)]
[Authorize(Policy = PostsModule.ManagePolicy)]
public sealed class AdminPostsController(ISender sender) : ControllerBase
{
    /// <summary>Reports waiting for a decision (or in another status), oldest first.</summary>
    [HttpGet("reports")]
    [ProducesResponseType<PagedResult<ReportDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<ReportDto>> ListReports(
        [FromQuery] ReportStatus status = ReportStatus.Open,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListReportsQuery(status, page, pageSize), cancellationToken);

    /// <summary>Dismiss the report, or hide the content (which closes every open report about it).</summary>
    [HttpPost("reports/{id:guid}/resolve")]
    [ProducesResponseType<ReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ReportDto> ResolveReport(Guid id, ResolveReportRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ResolveReportCommand(id, request.Action, request.Note), cancellationToken);

    /// <summary>Any post, including hidden ones and those whose author is deactivated.</summary>
    [HttpGet("posts/{id:guid}")]
    [ProducesResponseType<PostDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PostDto> GetPost(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetPostQuery(id, AsModerator: true), cancellationToken);

    /// <summary>Make a hidden post visible again.</summary>
    [HttpPost("posts/{id:guid}/unhide")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnhidePost(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new UnhidePostCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Delete anyone's post and its photos and videos.</summary>
    [HttpDelete("posts/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePost(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeletePostCommand(id, AsModerator: true), cancellationToken);
        return NoContent();
    }

    /// <summary>Delete anyone's comment.</summary>
    [HttpDelete("posts/{id:guid}/comments/{commentId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteComment(Guid id, Guid commentId, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteCommentCommand(id, commentId, AsModerator: true), cancellationToken);
        return NoContent();
    }
}
