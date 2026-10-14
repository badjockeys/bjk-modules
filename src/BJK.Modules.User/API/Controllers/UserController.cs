using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.API.Contracts;
using BJK.Modules.User.UseCases;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Get;
using BJK.Modules.User.UseCases.List;
using BJK.Modules.User.UseCases.Reset;
using BJK.Modules.User.UseCases.Update;
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
    /// <summary>Registers a new user (sign-in account + profile).</summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(AddUserRequest request, CancellationToken cancellationToken)
    {
        var id = await sender.Send(
            new AddUserCommand(request.Email, request.Password, request.DisplayName),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id }, new CreatedUserResponse(id));
    }

    /// <summary>Lists active users. Needs the bjk.users.manage scope.</summary>
    [HttpGet]
    [Authorize(Policy = UserModule.ManagePolicy)]
    [ProducesResponseType<PagedResult<UserDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<UserDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        await sender.Send(new ListUsersQuery(page, pageSize), cancellationToken);

    /// <summary>The signed-in user's own profile.</summary>
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

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new UpdateUserCommand(id, request.DisplayName), cancellationToken);
        return NoContent();
    }

    /// <summary>Soft-deletes the user and disables their sign-in.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteUserCommand(id), cancellationToken);
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
