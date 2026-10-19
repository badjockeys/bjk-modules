using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using BJK.Modules.User.API.Contracts;
using BJK.Modules.User.Domain;
using BJK.Modules.User.UseCases;
using BJK.Modules.User.UseCases.Add;
using BJK.Modules.User.UseCases.AgeChecks;
using BJK.Modules.User.UseCases.Confirm;
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
    /// <summary>
    /// Registers a new user (sign-in account) aged 13 or over. Before the account can get tokens, the user signs in
    /// on the identity server's pages and passes the liveness check. The app then creates their profile in bjk-core.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(AddUserRequest request, CancellationToken cancellationToken)
    {
        var id = await sender.Send(new AddUserCommand(request.Email, request.Password, request.DateOfBirth), cancellationToken);
        return Created((string?)null, new CreatedUserResponse(id));
    }

    /// <summary>The signed-in user's own account.</summary>
    [HttpGet("me")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<UserDto> GetCurrent(CancellationToken cancellationToken) =>
        await sender.Send(new GetCurrentUserQuery(), cancellationToken);

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

    /// <summary>
    /// My age check (18+, to view sensitive posts): status, attempts left, and whether the selfie or photo-ID route is
    /// available. Under 18 by date of birth, neither is (<c>underAgeByDateOfBirth</c>).
    /// </summary>
    [HttpGet("me/age-check")]
    [ProducesResponseType<AgeCheckDto>(StatusCodes.Status200OK)]
    public async Task<AgeCheckDto> GetAgeCheck(CancellationToken cancellationToken) =>
        await sender.Send(new GetMyAgeCheckQuery(), cancellationToken);

    /// <summary>
    /// Verify my age to view sensitive posts, with a 3–5 second head-turn selfie video (multipart field <c>video</c>).
    /// The video is checked in memory and wiped straight away; only the outcome is kept. Once passed, new tokens carry
    /// age_verified=true. Refused (400) when the date of birth says I'm under 18.
    /// </summary>
    [HttpPost("me/age-check/face")]
    [RequestSizeLimit(AgeCheckUploads.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = AgeCheckUploads.MaxRequestBytes, MemoryBufferThreshold = AgeCheckUploads.MaxRequestBytes)]
    [ProducesResponseType<AgeCheckDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<AgeCheckDto> SubmitFaceAgeCheck(IFormFile? video, CancellationToken cancellationToken)
    {
        var (bytes, contentType) = await AgeCheckUploads.ReadAsync(video, cancellationToken);
        return await sender.Send(new SubmitFaceAgeCheckCommand(bytes, contentType), cancellationToken);
    }

    /// <summary>Check my age with a photo of my ID instead (multipart field <c>image</c>). Wiped straight away; only the outcome is kept.</summary>
    [HttpPost("me/age-check/document")]
    [RequestSizeLimit(AgeCheckUploads.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = AgeCheckUploads.MaxRequestBytes, MemoryBufferThreshold = AgeCheckUploads.MaxRequestBytes)]
    [ProducesResponseType<AgeCheckDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<AgeCheckDto> SubmitDocumentAgeCheck(IFormFile? image, CancellationToken cancellationToken)
    {
        var (bytes, contentType) = await AgeCheckUploads.ReadAsync(image, cancellationToken);
        return await sender.Send(new SubmitDocumentAgeCheckCommand(bytes, contentType), cancellationToken);
    }

    /// <summary>Email me another link to confirm my email address.</summary>
    [HttpPost("me/confirmation-email")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendConfirmationEmail(CancellationToken cancellationToken)
    {
        await sender.Send(new SendEmailConfirmationCommand(), cancellationToken);
        return Accepted();
    }

    /// <summary>Confirm an email address with the token from the confirmation email. Anonymous.</summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ConfirmEmailCommand(request.UserId, request.Token), cancellationToken);
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
