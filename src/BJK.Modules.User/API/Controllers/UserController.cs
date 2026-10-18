using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.API.Contracts;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Deactivate;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Get;
using BJK.Modules.User.UseCases.List;
using BJK.Modules.User.UseCases.Reactivate;
using BJK.Modules.User.UseCases.Reset;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.User.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(AuthenticationSchemes = ModuleAuthentication.Scheme)]
public sealed class UserController(ISender sender) : ControllerBase
{
    /// <summary>Registers a new user (sign-in account). The app then creates their profile in bjk-core.</summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(AddUserRequest request, CancellationToken cancellationToken)
    {
        var id = await sender.Send(new AddUserCommand(request.Email, request.Password), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id }, new CreatedUserResponse(id));
    }

    /// <summary>Lists accounts, optionally by status. Needs the bjk.users.manage scope.</summary>
    [HttpGet]
    [Authorize(Policy = UserModule.ManagePolicy)]
    [ProducesResponseType<PagedResult<UserDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<UserDto>> List(
        [FromQuery] UserAccountStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListUsersQuery(status, page, pageSize), cancellationToken);

    /// <summary>The signed-in user's own account.</summary>
    [HttpGet("me")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> GetCurrent(CancellationToken cancellationToken) =>
        await sender.Send(new GetCurrentUserQuery(), cancellationToken);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> Get(Guid id, CancellationToken cancellationToken) =>
        await sender.Send(new GetUserQuery(id), cancellationToken);

    /// <summary>
    /// Deactivate my account (confirm with my password). I'm signed out, my content is hidden, and everything is
    /// deleted after 90 days unless I reactivate.
    /// </summary>
    [HttpPost("me/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeactivateMine(DeactivateAccountRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new DeactivateMyAccountCommand(request.Password), cancellationToken);
        return NoContent();
    }

    /// <summary>Reactivate a deactivated account within its grace period. Anonymous: the account can't sign in yet.</summary>
    [HttpPost("reactivate")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reactivate(ReactivateAccountRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReactivateAccountCommand(request.Email, request.Password), cancellationToken);
        return NoContent();
    }

    /// <summary>Admin: deactivate someone's account.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = UserModule.ManagePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeactivateUserCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Admin: reactivate someone's deactivated account.</summary>
    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = UserModule.ManagePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReactivateUser(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new ReactivateUserCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Admin: permanently delete an account and all its data now, skipping the grace period.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = UserModule.ManagePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteNow(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteUserNowCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>"Forgot password". Always returns 202, whether or not the email is registered.</summary>
    [HttpPost("password-reset")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestPasswordReset(
        RequestPasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        await sender.Send(new RequestPasswordResetCommand(request.Email), cancellationToken);
        return Accepted();
    }

    /// <summary>Sets a new password using the emailed reset token.</summary>
    [HttpPost("password-reset/confirm")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ResetPasswordCommand(request.Email, request.Token, request.NewPassword), cancellationToken);
        return NoContent();
    }
}
