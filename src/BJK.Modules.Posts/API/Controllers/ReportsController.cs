using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Shared.Safety;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BJK.Modules.Posts.API.Controllers;

/// <summary>
/// Reports that aren't about one post's own endpoints: the public "Report content / I'm in this clip" form, and
/// reports about profiles. Every report lands in the same moderators' queue (<c>GET /api/admin/reports</c>).
/// </summary>
[ApiController]
public sealed class ReportsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// The public report form, for anyone (no account needed). Send a Cloudflare Turnstile token in the
    /// X-Captcha-Token header. Returns a reference, the priority and when a decision is due; an acknowledgement is
    /// emailed to the reporter.
    /// </summary>
    [HttpPost("api/reports/public")]
    [Authorize(Policy = PostsModule.ReadPolicy)]
    [ProducesResponseType<ReportReceipt>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ReportReceipt> ReportPublicly(
        PublicReportRequest request,
        [FromHeader(Name = CaptchaOptions.HeaderName)] string? captchaToken,
        CancellationToken cancellationToken) =>
        await sender.Send(
            new PublicReportCommand(
                request.Target,
                request.CommentId,
                request.Reason,
                request.Details,
                request.ReporterName,
                request.ReporterEmail,
                request.Consent,
                captchaToken,
                ClientIp()),
            cancellationToken);

    /// <summary>Report a user's profile (handle, name, bio or avatar). Signed-in users, once per profile.</summary>
    [HttpPost("api/profiles/{handle}/reports")]
    [Authorize(Policy = PostsModule.UsePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReportProfile(string handle, ReportRequest request, CancellationToken cancellationToken)
    {
        await sender.Send(new ReportProfileCommand(handle, request.Reason, request.Details), cancellationToken);
        return NoContent();
    }

    // Behind Cloudflare the visitor's address is in CF-Connecting-IP; otherwise use the connection's.
    private string? ClientIp() =>
        Request.Headers.TryGetValue("CF-Connecting-IP", out var forwarded) && !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.ToString()
            : HttpContext.Connection.RemoteIpAddress?.ToString();
}
