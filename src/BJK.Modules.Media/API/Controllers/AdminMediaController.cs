using BJK.Modules.Media.UseCases;
using BJK.Modules.Media.UseCases.Delete;
using BJK.Modules.Media.UseCases.Get;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>Delete anyone's media: the files and the record.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteMediaCommand(id, AsModerator: true), cancellationToken);
        return NoContent();
    }
}
