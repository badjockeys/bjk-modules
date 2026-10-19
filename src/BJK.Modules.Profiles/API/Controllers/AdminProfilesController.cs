using BJK.Modules.Profiles.UseCases;
using BJK.Modules.Profiles.UseCases.Moderation;
using BJK.Modules.Shared.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Profiles.API.Controllers;

/// <summary>
/// Moderation, for the back-office. Needs the bjk.profiles.manage scope and, for a user, the Moderator or Admin role
/// with two-factor sign-in. Every action is recorded in the moderation audit log.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix + "/profiles")]
[Authorize(Policy = ProfilesModule.ManagePolicy)]
public sealed class AdminProfilesController(ISender sender) : ControllerBase
{
    /// <summary>Any profile, including deactivated ones.</summary>
    [HttpGet("{handle}")]
    [ProducesResponseType<ModeratedProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ModeratedProfileDto> Get(string handle, CancellationToken cancellationToken) =>
        await sender.Send(new GetProfileForModerationQuery(handle), cancellationToken);

    /// <summary>Reset the display name to the handle and remove the bio, location and avatar. A reason is required.</summary>
    [HttpPost("{handle}/reset")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ProfileDto> Reset(string handle, ModerationReasonRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new ResetProfileCommand(handle, request.Reason), cancellationToken);
}

/// <summary>Why a moderator took an action. Recorded in the moderation audit log.</summary>
public sealed record ModerationReasonRequest(string Reason);
