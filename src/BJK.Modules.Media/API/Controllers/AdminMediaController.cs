using BJK.Modules.Media.UseCases;
using BJK.Modules.Media.UseCases.Delete;
using BJK.Modules.Media.UseCases.Get;
using BJK.Modules.Media.UseCases.KnownImages;
using BJK.Modules.Media.UseCases.Review;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using BJK.Modules.Media.Domain;

namespace BJK.Modules.Media.API.Controllers;

/// <summary>
/// Moderation, for the back-office. Needs the bjk.media.manage scope and, for a user, the Moderator or Admin role
/// with two-factor sign-in.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix + "/media")]
[Authorize(Policy = MediaModule.ManagePolicy)]
public sealed class AdminMediaController(ISender sender) : ControllerBase
{
    /// <summary>Any media, in any state.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<MediaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<MediaDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetMediaQuery(id, AsModerator: true), cancellationToken);

    /// <summary>Delete anyone's media: the files and the record. A reason is required (<c>?reason=</c>) and audited.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteMediaCommand(id, AsModerator: true, reason), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The review queue: media whose automatic blurring failed or wasn't sure (or that a moderator re-processed),
    /// oldest first. It can't be posted until approved.
    /// </summary>
    [HttpGet("review")]
    [ProducesResponseType<PagedResult<MediaReviewDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<MediaReviewDto>> ListForReview(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListMediaForReviewQuery(page, pageSize), cancellationToken);

    /// <summary>One media item with signed links (15 minutes) to its blurred preview.</summary>
    [HttpGet("{id:guid}/review")]
    [ProducesResponseType<MediaReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<MediaReviewDto> GetForReview(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetMediaForReviewQuery(id), cancellationToken);

    /// <summary>A signed link (15 minutes) to the unblurred original. Admins only; every use is audited.</summary>
    [HttpGet("{id:guid}/original")]
    [ProducesResponseType<OriginalDownload>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<OriginalDownload> GetOriginal(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetOriginalForReviewQuery(id), cancellationToken);

    /// <summary>Approve the blurred files: they're published and the media becomes Ready.</summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<MediaReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<MediaReviewDto> Approve(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new ApproveMediaCommand(id), cancellationToken);

    /// <summary>Reject media waiting for review: it becomes Failed, and the uploader sees the reason.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<MediaReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<MediaReviewDto> Reject(Guid id, RejectMediaRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new RejectMediaCommand(id, request.Reason), cancellationToken);

    /// <summary>
    /// Add blur regions (fractions of the picture, plus a time range for videos) and process the media again. The
    /// result comes back to the review queue.
    /// </summary>
    [HttpPost("{id:guid}/redactions")]
    [ProducesResponseType<MediaReviewDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddRedactions(Guid id, AddRedactionsRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new AddManualRedactionsCommand(id, request.Regions ?? []), cancellationToken);
        return Accepted(result);
    }

    /// <summary>
    /// Block copies of this media: its file hash and perceptual hashes go on the known-image list, so the same picture
    /// (or a resized or recompressed copy) is blocked if it's uploaded again. Use after removing it. Returns how many
    /// hashes were added.
    /// </summary>
    [HttpPost("{id:guid}/block-copies")]
    [ProducesResponseType<BlockCopiesResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<BlockCopiesResult> BlockCopies(Guid id, BlockCopiesRequest request, CancellationToken cancellationToken) =>
        new(await sender.Send(new BlockMediaCopiesCommand(id, request.Reason), cancellationToken));
}

/// <param name="Reason">Why, for the audit log.</param>
public sealed record BlockCopiesRequest(string Reason);

/// <param name="HashesAdded">Hashes added to the known-image list.</param>
public sealed record BlockCopiesResult(int HashesAdded);

/// <param name="Reason">Shown to the uploader.</param>
public sealed record RejectMediaRequest(string Reason);

/// <param name="Regions">Regions to blur.</param>
public sealed record AddRedactionsRequest(IReadOnlyList<ManualRedaction>? Regions);
