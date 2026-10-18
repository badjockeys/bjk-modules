using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Communications.Infrastructure.Email;

/// <summary>
/// Sends through SendGrid's v3 Mail Send API. Click tracking is switched off so links carrying sign-in, confirmation
/// and reset codes go straight to us rather than through a SendGrid redirect.
/// </summary>
internal sealed class SendGridEmailSender(
    HttpClient client,
    IOptions<EmailOptions> options,
    ProviderRetry retry,
    ILogger<SendGridEmailSender> logger)
    : IEmailSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.To);

        var settings = options.Value;
        var payload = BuildPayload(message, settings);
        var endpoint = new Uri(settings.SendGrid.BaseUrl, "/v3/mail/send");

        using var response = await retry.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", settings.SendGrid.ApiKey) },
                Content = JsonContent.Create(payload, options: Json),
            },
            "SendGrid",
            logger,
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var messageId = response.Headers.TryGetValues("X-Message-Id", out var ids) ? ids.FirstOrDefault() : null;
            logger.LogInformation(
                "Email \"{Subject}\" accepted by SendGrid for {To} (message id {MessageId}).",
                message.Subject,
                Redact.Email(message.To),
                messageId);
            return;
        }

        var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        logger.LogError(
            "SendGrid rejected email \"{Subject}\" for {To}: HTTP {Status} {Detail}",
            message.Subject,
            Redact.Email(message.To),
            (int)response.StatusCode,
            detail);
        throw new MessageDeliveryException(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "SendGrid refused the API key. Check Email:SendGrid:ApiKey has the Mail Send permission."
                : $"SendGrid rejected the email (HTTP {(int)response.StatusCode}): {detail}",
            isTransient: false);
    }

    internal static SendGridPayload BuildPayload(EmailMessage message, EmailOptions settings)
    {
        var content = new List<SendGridContent> { new("text/plain", message.TextBody) };
        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            // SendGrid requires text/plain before text/html.
            content.Add(new SendGridContent("text/html", message.HtmlBody));
        }

        return new SendGridPayload(
            Personalizations: [new SendGridPersonalization([new SendGridAddress(message.To, null)])],
            From: new SendGridAddress(settings.FromAddress!, settings.FromName),
            ReplyTo: string.IsNullOrWhiteSpace(settings.ReplyToAddress) ? null : new SendGridAddress(settings.ReplyToAddress, null),
            Subject: message.Subject,
            Content: content,
            TrackingSettings: new SendGridTrackingSettings(
                new SendGridSwitch(Enable: false, EnableText: false),
                new SendGridOpenTracking(Enable: false)),
            MailSettings: settings.SendGrid.SandboxMode ? new SendGridMailSettings(new SendGridSandbox(true)) : null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<SendGridErrorResponse>(Json, cancellationToken).ConfigureAwait(false);
            var messages = body?.Errors?.Select(e => e.Field is null ? e.Message : $"{e.Field}: {e.Message}").ToArray();
            if (messages is { Length: > 0 })
            {
                return string.Join("; ", messages);
            }
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return response.ReasonPhrase ?? "no details";
    }
}

internal sealed record SendGridPayload(
    IReadOnlyList<SendGridPersonalization> Personalizations,
    SendGridAddress From,
    [property: JsonPropertyName("reply_to")] SendGridAddress? ReplyTo,
    string Subject,
    IReadOnlyList<SendGridContent> Content,
    [property: JsonPropertyName("tracking_settings")] SendGridTrackingSettings TrackingSettings,
    [property: JsonPropertyName("mail_settings")] SendGridMailSettings? MailSettings);

internal sealed record SendGridPersonalization(IReadOnlyList<SendGridAddress> To);

internal sealed record SendGridAddress(string Email, string? Name);

internal sealed record SendGridContent(string Type, string Value);

internal sealed record SendGridTrackingSettings(
    [property: JsonPropertyName("click_tracking")] SendGridSwitch ClickTracking,
    [property: JsonPropertyName("open_tracking")] SendGridOpenTracking OpenTracking);

internal sealed record SendGridSwitch(
    bool Enable,
    [property: JsonPropertyName("enable_text")] bool EnableText);

internal sealed record SendGridOpenTracking(bool Enable);

internal sealed record SendGridMailSettings([property: JsonPropertyName("sandbox_mode")] SendGridSandbox SandboxMode);

internal sealed record SendGridSandbox(bool Enable);

internal sealed record SendGridErrorResponse(IReadOnlyList<SendGridError>? Errors);

internal sealed record SendGridError(string? Message, string? Field);
