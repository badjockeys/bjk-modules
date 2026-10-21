using BJK.Modules.Media.UseCases;
using BJK.Modules.Media.UseCases.Delete;
using BJK.Modules.Media.UseCases.Get;
using BJK.Modules.Media.UseCases.Upload;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using BJK.Modules.Media.UseCases.Describe;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Media.API.Controllers;

[ApiController]
[Route("api/media")]
[Authorize(AuthenticationSchemes = ModuleAuthentication.Scheme, Policy = MediaModule.UsePolicy)]
public sealed class MediaController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Start an upload. Returns a link valid for a few minutes: PUT the file to it with the headers
    /// given, then call <c>POST /api/media/{id}/complete</c>.
    /// </summary>
    [HttpPost("uploads")]
    [ProducesResponseType<RequestUploadResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestUpload(RequestUploadRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RequestUploadCommand(request.FileName, request.ContentType, request.SizeBytes),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = result.MediaId }, result);
    }

    /// <summary>Tell the API the file has been uploaded. Queues it for processing.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<MediaDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken cancellationToken)
    {
        var media = await sender.Send(new CompleteUploadCommand(id), cancellationToken);
        return Accepted(media);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<MediaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<MediaDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetMediaQuery(id), cancellationToken);

    /// <summary>The signed-in user's uploads, including ones still processing or failed.</summary>
    [HttpGet("mine")]
    [ProducesResponseType<PagedResult<MediaDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<MediaDto>> ListMine(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListMyMediaQuery(page, pageSize), cancellationToken);

    /// <summary>
    /// "Suggest a description": queues a draft title and description for your photos or video (all ready), from blurred
    /// pictures and what you've filled in. At most 10 a day (429 with <c>code</c> <c>description_limit</c> after that).
    /// Poll <c>GET /api/media/descriptions/{id}</c> until it's Ready or Failed (usually seconds; up to a couple of
    /// minutes after a quiet spell).
    /// </summary>
    [HttpPost("descriptions")]
    [ProducesResponseType<DescriptionDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestDescription(RequestDescriptionCommand request, CancellationToken cancellationToken)
    {
        var description = await sender.Send(request, cancellationToken);
        return AcceptedAtAction(nameof(GetDescription), new { id = description.Id }, description);
    }

    /// <summary>A "Suggest a description" request you made: Queued, Ready (with the draft) or Failed (with why).</summary>
    [HttpGet("descriptions/{id:guid}")]
    [ProducesResponseType<DescriptionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<DescriptionDto> GetDescription(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetDescriptionQuery(id), cancellationToken);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteMediaCommand(id), cancellationToken);
        return NoContent();
    }
}

public sealed record RequestUploadRequest(string FileName, string ContentType, long SizeBytes);
