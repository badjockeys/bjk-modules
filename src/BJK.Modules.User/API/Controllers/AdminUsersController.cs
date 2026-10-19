using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.API.Contracts;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases;
using BJK.Modules.User.UseCases.AgeChecks;
using BJK.Modules.User.UseCases.Deactivate;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Get;
using BJK.Modules.User.UseCases.List;
using BJK.Modules.User.UseCases.Reactivate;
using BJK.Modules.User.UseCases.Roles;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.User.API.Controllers;

/// <summary>
/// Account management for the back-office. Needs the bjk.users.manage scope and, for a user, the Moderator or Admin
/// role with two-factor sign-in. Moderators can view, deactivate and reactivate ordinary users; only admins can act
/// on staff, delete accounts or set roles. In production Cloudflare Access only lets the back-office reach /api/admin.
/// </summary>
[ApiController]
[Route(ModuleAuthentication.AdminRoutePrefix + "/users")]
[Authorize(Policy = UserModule.ManagePolicy)]
public sealed class AdminUsersController(ISender sender) : ControllerBase
{
    /// <summary>Accounts, newest first, optionally by status.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<UserDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<UserDto>> List(
        [FromQuery] UserAccountStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListUsersQuery(status, page, pageSize), cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetUserQuery(id), cancellationToken);

    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeactivateUserCommand(id), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new ReactivateUserCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Admins: permanently delete an account and all its data now, skipping the grace period.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNow(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteUserNowCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Admins: reset the account's age check so the user takes it again. The status (never the video) is in the
    /// account's <c>ageCheck</c>.
    /// </summary>
    [HttpPost("{id:guid}/age-check/reset")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> ResetAgeCheck(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new ResetAgeCheckCommand(id), cancellationToken);

    /// <summary>Admins: set the account's staff roles (none, Moderator, Admin).</summary>
    [HttpPut("{id:guid}/roles")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> SetRoles(Guid id, SetRolesRequest request, CancellationToken cancellationToken) =>
        await sender.Send(new SetRolesCommand(id, request.Roles), cancellationToken);
}
