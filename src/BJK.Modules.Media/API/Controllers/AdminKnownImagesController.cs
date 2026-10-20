using BJK.Modules.Media.Domain;
using BJK.Modules.Media.UseCases.KnownImages;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Media.API.Controllers;

/// <summary>
/// The known-image list and the uploads it blocked. Admins only (bjk.media.manage scope, Admin role, two-factor
/// sign-in). The list itself is in SQL; hash list providers are synced into it by the media worker.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix + "/known-images")]
[Authorize(Policy = MediaModule.ManagePolicy)]
public sealed class AdminKnownImagesController(ISender sender) : ControllerBase
{
    /// <summary>Uploads blocked because they matched a known image, newest first. No pictures are kept.</summary>
    [HttpGet("matches")]
    [ProducesResponseType<PagedResult<KnownImageMatchDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<KnownImageMatchDto>> ListMatches(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListKnownImageMatchesQuery(page, pageSize), cancellationToken);

    /// <summary>Record that a match was reported to the authorities, with their reference.</summary>
    [HttpPost("matches/{id:guid}/reported")]
    [ProducesResponseType<KnownImageMatchDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<KnownImageMatchDto> MarkReported(Guid id, MarkReportedRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new MarkKnownImageMatchReportedCommand(id, request.Reference), cancellationToken);

    /// <summary>The hashes, newest first; <c>?source=</c> for one source's (e.g. Moderation, Test, IWF).</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<KnownImageHashDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<KnownImageHashDto>> ListHashes(
        [FromQuery] string? source = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListKnownImageHashesQuery(source, page, pageSize), cancellationToken);

    /// <summary>Add a hash by hand (e.g. one given by the police).</summary>
    [HttpPost]
    [ProducesResponseType<KnownImageHashDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<KnownImageHashDto> Add(AddKnownImageHashRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new AddKnownImageHashCommand(request.Kind, request.Value, request.Reference), cancellationToken);

    /// <summary>Remove a hash added by moderators or for testing.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new RemoveKnownImageHashCommand(id), cancellationToken);
        return NoContent();
    }
}

/// <param name="Reference">The authority's reference for the report.</param>
public sealed record MarkReportedRequest(string Reference);

/// <param name="Kind">Pdq, Sha256 or Md5.</param>
/// <param name="Value">The hash, hex.</param>
/// <param name="Reference">Where it came from, optional.</param>
public sealed record AddKnownImageHashRequest(KnownHashKind Kind, string Value, string? Reference);
