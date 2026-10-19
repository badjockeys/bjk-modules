using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Reels;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Reels: a full-screen feed of video posts with a daily allowance by plan. Logged-out viewers are counted by the
/// <c>X-Viewer-Id</c> header (the web app's signed, httpOnly cookie, or the mobile app's install id), else by IP
/// address; the host must apply forwarded headers so the IP is the viewer's, not Cloudflare's.
/// </summary>
[ApiController]
[Route("api/reels")]
public sealed class ReelsController(ISender sender) : ControllerBase
{
    /// <summary>The header logged-out viewers' apps send.</summary>
    public const string ViewerIdHeader = "X-Viewer-Id";

    /// <summary>
    /// A page of reels, best ranked first, with the caller's plays left today (<c>playsRemainingToday</c>, null for
    /// unlimited). When none are left: 403 with <c>code</c> = <c>daily_limit</c> and <c>resetsAt</c>.
    /// </summary>
    /// <param name="cursor">From the previous page's <c>nextCursor</c>.</param>
    /// <param name="category">Only this kind of incident.</param>
    /// <param name="pageSize">Reels per page, up to 20 (and at most the plays left today).</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<ReelsPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ReelsPage> List(
        [FromQuery] string? cursor,
        [FromQuery] IncidentCategory? category,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListReelsQuery(cursor, category, Viewer(), pageSize), cancellationToken);

    /// <summary>
    /// Report that the viewer has watched a reel for about 3 seconds. Idempotent per viewer, reel and UK day. 403
    /// <c>daily_limit</c> if today's allowance is used up and this reel wasn't already played today.
    /// </summary>
    /// <param name="postId">The video post.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPost("{postId:guid}/play")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<ReelPlayResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ReelPlayResult> Play(Guid postId, CancellationToken cancellationToken) =>
        await sender.Send(new RecordReelPlayCommand(postId, Viewer()), cancellationToken);

    private ViewerContext Viewer() => new(
        Request.Headers[ViewerIdHeader].FirstOrDefault(),
        HttpContext.Connection.RemoteIpAddress?.ToString());
}
