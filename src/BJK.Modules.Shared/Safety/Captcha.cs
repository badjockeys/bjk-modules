using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Safety;

public enum CaptchaResult
{
    Passed,
    Failed,

    /// <summary>The CAPTCHA service couldn't be reached, so the token couldn't be checked.</summary>
    Unavailable,
}

/// <summary>
/// Checks a CAPTCHA token sent by an anonymous caller in the <see cref="CaptchaOptions.HeaderName"/> header, such as
/// on the public "Report content" form, with Cloudflare Turnstile.
/// </summary>
public interface ICaptchaVerifier
{
    Task<CaptchaResult> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken);
}

/// <summary>
/// CAPTCHA settings. Section <c>Captcha</c>, with the same key names as the identity server, so one secret serves both.
/// </summary>
public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    /// <summary>Request header the client puts the Cloudflare Turnstile token in.</summary>
    public const string HeaderName = "X-Captcha-Token";

    /// <summary>
    /// Turnstile secret key: a container secret when deployed, user secrets locally. Cloudflare's test key
    /// <c>1x0000000000000000000000000000000AA</c> accepts every token; <c>2x0000000000000000000000000000000AA</c> rejects every token.
    /// </summary>
    public string? TurnstileSecretKey { get; set; }

    public Uri TurnstileVerifyUrl { get; set; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");
}

internal sealed class CaptchaOptionsValidator : IValidateOptions<CaptchaOptions>
{
    public ValidateOptionsResult Validate(string? name, CaptchaOptions options) =>
        string.IsNullOrWhiteSpace(options.TurnstileSecretKey)
            ? ValidateOptionsResult.Fail(
                "Captcha:TurnstileSecretKey is required. Locally, use Cloudflare's test key from user-secrets.template.json "
                + "(1x0000000000000000000000000000000AA, which accepts every token).")
            : ValidateOptionsResult.Success;
}

/// <summary>Checks a Cloudflare Turnstile token with Cloudflare's siteverify endpoint. Tokens are single-use.</summary>
internal sealed class TurnstileCaptchaVerifier(
    HttpClient http,
    IOptions<CaptchaOptions> options,
    ILogger<TurnstileCaptchaVerifier> logger)
    : ICaptchaVerifier
{
    public async Task<CaptchaResult> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CaptchaResult.Failed;
        }

        var fields = new Dictionary<string, string>
        {
            ["secret"] = options.Value.TurnstileSecretKey ?? string.Empty,
            ["response"] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            fields["remoteip"] = remoteIp;
        }

        using var form = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await http.PostAsync(options.Value.TurnstileVerifyUrl, form, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
            if (result?.Success == true)
            {
                return CaptchaResult.Passed;
            }

            logger.LogInformation("CAPTCHA rejected: {ErrorCodes}", string.Join(", ", result?.ErrorCodes ?? []));
            return CaptchaResult.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Couldn't reach Cloudflare Turnstile to check a CAPTCHA token.");
            return CaptchaResult.Unavailable;
        }
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
