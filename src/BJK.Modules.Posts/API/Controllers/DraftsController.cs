using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.UseCases.Drafts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Drafts of posts, saved to finish later from any device: up to 5 each, deleted after 30 days without being saved.
/// Publish one by sending its id as <c>draftId</c> when creating the post.
/// </summary>
[ApiController]
[Route("api/posts/drafts")]
[Authorize(Policy = PostsModule.UsePolicy)]
public sealed class DraftsController(ISender sender) : ControllerBase
{
    /// <summary>Your drafts, last saved first.</summary>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DraftDto>>(StatusCodes.Status200OK)]
    public async Task<IReadOnlyList<DraftDto>> List(CancellationToken cancellationToken) =>
        await sender.Send(new ListDraftsQuery(), cancellationToken);

    /// <summary>One of your drafts.</summary>
    /// <param name="id">The draft's id.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<DraftDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<DraftDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetDraftQuery(id), cancellationToken);

    /// <summary>Save a new draft. You can keep up to 5.</summary>
    /// <param name="command">What's in it so far. Everything is optional, but it can't be empty.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPost]
    [ProducesResponseType<DraftDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(SaveDraftCommand command, CancellationToken cancellationToken)
    {
        var draft = await sender.Send(command with { DraftId = null }, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = draft.Id }, draft);
    }

    /// <summary>Save over one of your drafts (replaces everything in it, and restarts its 30 days).</summary>
    /// <param name="id">The draft's id.</param>
    /// <param name="command">What's in it now.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<DraftDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<DraftDto> Update(Guid id, SaveDraftCommand command, CancellationToken cancellationToken) =>
        await sender.Send(command with { DraftId = id }, cancellationToken);

    /// <summary>Delete one of your drafts, with its photos and videos.</summary>
    /// <param name="id">The draft's id.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteDraftCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>How many drafts each person can keep, and for how long. For the composer to explain the limits.</summary>
    [HttpGet("limits")]
    [ProducesResponseType<DraftLimitsDto>(StatusCodes.Status200OK)]
    public DraftLimitsDto Limits() => new(PostDraft.MaxPerAuthor, (int)PostDraft.Lifetime.TotalDays);
}

/// <summary>The draft limits.</summary>
/// <param name="MaxDrafts">How many drafts each person can keep.</param>
/// <param name="KeptForDays">Days a draft is kept after it was last saved.</param>
public sealed record DraftLimitsDto(int MaxDrafts, int KeptForDays);
