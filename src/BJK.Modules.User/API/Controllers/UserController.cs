using System.Security.Claims;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.User.API.Contracts;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.Delete;
using BJK.Modules.User.UseCases.Reset;
using BJK.Modules.User.UseCases.Update;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.User.API.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UserController(ISender sender) : ControllerBase
{
    /// <summary>Creates the profile for the signed-in identity server account.</summary>
    [HttpPost]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(AddUserRequest request, CancellationToken cancellationToken)
    {
        var email = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var id = await sender.Send(new AddUserCommand(CallerIdentityId(), email, request.DisplayName), cancellationToken);

        return Created($"/api/users/{id}", new CreatedUserResponse(id));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new UpdateUserCommand(id, CallerIdentityId(), request.DisplayName), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/password-reset")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new ResetUserPasswordCommand(id, CallerIdentityId()), cancellationToken);
        return Accepted();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteUserCommand(id, CallerIdentityId()), cancellationToken);
        return NoContent();
    }

    // "sub" from the identity server token. ASP.NET Core maps it to NameIdentifier unless MapInboundClaims is off.
    private string CallerIdentityId() =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new ForbiddenException("The access token has no subject.");
}
