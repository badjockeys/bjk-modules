using BJK.Modules.Media.UseCases.Vault;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Media.API.Controllers;

/// <summary>
/// Your vault (owner, 23 Oct 2026): your original clips, unblurred with their sound and GPS, kept privately for you as
/// evidence. The originals of your posts go in automatically while there's room, and you can upload clips straight to
/// it. Free keeps clips 30 days in 2 GB, Premium 12 months in 25 GB, Premium + 3 years in 100 GB. Only you can see it.
/// </summary>
[ApiController]
[Route("api/vault")]
[Authorize(AuthenticationSchemes = ModuleAuthentication.Scheme, Policy = MediaModule.UsePolicy)]
public sealed class VaultController(ISender sender) : ControllerBase
{
    /// <summary>Your vault: your plan's limits, the space used, and a page of your clips, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<VaultDto>(StatusCodes.Status200OK)]
    public async Task<VaultDto> Get(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new GetVaultQuery(page, pageSize), cancellationToken);

    /// <summary>
    /// Start an upload straight to your vault. Returns a link valid for a few minutes: PUT the file to it with the
    /// headers given, then call <c>POST /api/vault/{id}/complete</c>. 400 if the vault hasn't room.
    /// </summary>
    [HttpPost("uploads")]
    [ProducesResponseType<VaultUploadResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestUpload(VaultUploadRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RequestVaultUploadCommand(request.FileName, request.ContentType, request.SizeBytes), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>The file has been uploaded: keeps it, if it arrived and fits.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<VaultClipDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<VaultClipDto> Complete(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new CompleteVaultUploadCommand(id), cancellationToken);

    /// <summary>A link, valid for 15 minutes, that downloads one of your clips.</summary>
    [HttpGet("{id:guid}/download")]
    [ProducesResponseType<VaultDownloadDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<VaultDownloadDto> Download(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetVaultDownloadQuery(id), cancellationToken);

    /// <summary>
    /// Turn keeping your posts' originals automatically on or off. Off: a post's originals are deleted once it's
    /// published. Clips already in your vault stay.
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType<VaultSettingsDto>(StatusCodes.Status200OK)]
    public async Task<VaultSettingsDto> UpdateSettings(VaultSettingsRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SetVaultSettingsCommand(request.AutoSave), cancellationToken);

    /// <summary>
    /// Links, valid for 15 minutes, that show your clips in the page (for thumbnails). Up to 50 at a time; ids that
    /// aren't your stored clips are left out.
    /// </summary>
    [HttpGet("previews")]
    [ProducesResponseType<IReadOnlyList<VaultPreviewDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IReadOnlyList<VaultPreviewDto>> Previews([FromQuery] Guid[] ids, CancellationToken cancellationToken) =>
        await sender.Send(new GetVaultPreviewsQuery(ids), cancellationToken);

    /// <summary>Delete one of your clips now.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteVaultClipCommand(id), cancellationToken);
        return NoContent();
    }
}

public sealed record VaultUploadRequest(string FileName, string ContentType, long SizeBytes);

public sealed record VaultSettingsRequest(bool AutoSave);
