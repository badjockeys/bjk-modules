using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Engagement;
using BJK.Modules.Posts.UseCases.Reels;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Votes, following posts, bookmarks, the reach of your own posts, view and share counting, and your settings for
/// your own posts.
/// </summary>
[ApiController]
[Route("api/posts")]
public sealed class EngagementController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Vote on a post ("Who's at fault?" or "How bad was it?", see the post's <c>vote.kind</c> and
    /// <c>vote.choices</c>), or change your vote, until it closes. Not on your own post. Returns the vote with results.
    /// </summary>
    [HttpPut("{id:guid}/vote")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<VoteSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoteSummaryDto>> Vote(Guid id, VoteRequest request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new CastVoteCommand(id, request.Choice), cancellationToken));

    /// <summary>Follow a post: you'll be told about new comments and the vote result.</summary>
    [HttpPut("{id:guid}/follow")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostFollowStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PostFollowStateDto>> Follow(Guid id, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new FollowPostCommand(id), cancellationToken));

    /// <summary>Stop following a post.</summary>
    [HttpDelete("{id:guid}/follow")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostFollowStateDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PostFollowStateDto>> Unfollow(Guid id, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UnfollowPostCommand(id), cancellationToken));

    /// <summary>Save a post to your bookmarks (only you can see them).</summary>
    [HttpPut("{id:guid}/bookmark")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<BookmarkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookmarkStateDto>> Save(Guid id, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new SavePostCommand(id), cancellationToken));

    /// <summary>Remove a post from your bookmarks.</summary>
    [HttpDelete("{id:guid}/bookmark")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<BookmarkStateDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BookmarkStateDto>> Unsave(Guid id, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new UnsavePostCommand(id), cancellationToken));

    /// <summary>Your saved posts, most recently saved first.</summary>
    [HttpGet("bookmarks")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<PostDto>>> Bookmarks(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await sender.Send(new ListBookmarksQuery(page, pageSize), cancellationToken));

    /// <summary>
    /// Count that the viewer saw these posts (in a feed for a second or more, or opened): once per viewer per post.
    /// Anyone; signed-out viewers send <c>X-Viewer-Id</c> as for reels.
    /// </summary>
    [HttpPost("views")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecordViews(ViewsRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new RecordViewsCommand(request.PostIds ?? [], Viewer()), cancellationToken);
        return NoContent();
    }

    /// <summary>Count that the viewer shared a post (share sheet or copied link): once per viewer per day.</summary>
    [HttpPost("{id:guid}/share")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordShare(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RecordShareCommand(id, Viewer()), cancellationToken);
        return NoContent();
    }

    /// <summary>How far your post reached: unique views, local views, reactions, comments and shares. Its author only.</summary>
    [HttpGet("{id:guid}/reach")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PostReachDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PostReachDto>> Reach(Guid id, CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetPostReachQuery(id), cancellationToken));

    /// <summary>Your settings for your own posts: when votes on them close.</summary>
    [HttpGet("preferences")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<AuthorPreferencesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthorPreferencesDto>> GetPreferences(CancellationToken cancellationToken) =>
        Ok(await sender.Send(new GetAuthorPreferencesQuery(), cancellationToken));

    /// <summary>Change when votes on your new posts close: 48 hours, 7 days or never.</summary>
    [HttpPut("preferences")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<AuthorPreferencesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthorPreferencesDto>> SetPreferences(SetAuthorPreferencesCommand request, CancellationToken cancellationToken) =>
        Ok(await sender.Send(request, cancellationToken));

    private ViewerContext Viewer() => new(
        Request.Headers[ReelsController.ViewerIdHeader].FirstOrDefault(),
        HttpContext.Connection.RemoteIpAddress?.ToString());
}

/// <summary>Your answer to a post's vote.</summary>
public sealed record VoteRequest(VoteChoice Choice);

/// <summary>Posts the viewer saw (1 to 50).</summary>
public sealed record ViewsRequest(IReadOnlyList<Guid>? PostIds);
