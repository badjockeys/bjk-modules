using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Timelines;
using BJK.Modules.Shared;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Home timelines: For you (anyone), Following, and the signed-in user's custom timelines (one or more types of bad
/// driving, optionally within a radius of a place; Free 1, Premium 5, Premium + 10).
/// Each action names its own policy: a class-level sign-in policy would be added to For you's public one (ASP.NET
/// Core requires both), which made For you refuse logged-out visitors.
/// </summary>
[ApiController]
[Route("api/timelines")]
public sealed class TimelinesController(ISender sender) : ControllerBase
{
    /// <summary>
    /// For you: the last 30 days' public posts ranked by reactions and comments fading with age, with posts near your
    /// home area higher; then older posts, newest first. Anyone can call this.
    /// </summary>
    [HttpGet("for-you")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<PostDto>> ForYou([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        await sender.Send(new ListForYouPostsQuery(page, pageSize), cancellationToken);

    /// <summary>Following: posts by the people you follow, and your own, newest first.</summary>
    [HttpGet("following")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<PostDto>> Following([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default) =>
        await sender.Send(new ListFollowingPostsQuery(page, pageSize), cancellationToken);

    /// <summary>Your custom timelines, in order, and how many your plan allows.</summary>
    [HttpGet]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<TimelinesDto>(StatusCodes.Status200OK)]
    public async Task<TimelinesDto> List(CancellationToken cancellationToken) =>
        await sender.Send(new ListTimelinesQuery(), cancellationToken);

    /// <summary>Add a custom timeline at the end. 400 when your plan's limit is reached.</summary>
    [HttpPost]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<TimelineDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateTimelineCommand command, CancellationToken cancellationToken)
    {
        var timeline = await sender.Send(command, cancellationToken);
        return Created($"/api/timelines/{timeline.Id}", timeline);
    }

    /// <summary>Put your custom timelines in this order (list each once).</summary>
    [HttpPut("order")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<TimelinesDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<TimelinesDto> Reorder(ReorderTimelinesCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command, cancellationToken);

    /// <summary>Change a custom timeline.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<TimelineDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<TimelineDto> Update(Guid id, TimelineRequest request, CancellationToken cancellationToken) =>
        await sender.Send(
            new UpdateTimelineCommand(id, request.Name, request.Types, request.PlaceName, request.Latitude, request.Longitude, request.RadiusKm),
            cancellationToken);

    /// <summary>Delete a custom timeline.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteTimelineCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>A custom timeline's posts, newest first.</summary>
    [HttpGet("{id:guid}/posts")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType<PagedResult<PostDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<PagedResult<PostDto>> Posts(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListTimelinePostsQuery(id, page, pageSize), cancellationToken);
}

/// <summary>A custom timeline's name, types and optional place (name, position and radius).</summary>
public sealed record TimelineRequest(
    string Name,
    IReadOnlyList<IncidentCategory> Types,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null);
