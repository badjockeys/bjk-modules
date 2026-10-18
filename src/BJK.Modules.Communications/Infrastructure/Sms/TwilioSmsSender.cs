using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BJK.Modules.Communications.Contracts;
using BJK.Modules.Communications.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Communications.Infrastructure.Sms;

/// <summary>Sends through Twilio's Programmable Messaging API (POST /2010-04-01/Accounts/{sid}/Messages.json).</summary>
internal sealed class TwilioSmsSender(
    HttpClient client,
    IOptions<SmsOptions> options,
    ProviderRetry retry,
    ILogger<TwilioSmsSender> logger)
    : ISmsSender
{
    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!PhoneNumbers.IsE164(message.To))
        {
            throw new ArgumentException("The phone number must be in E.164 format, e.g. +447700900123.", nameof(message));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message.Body);

        var settings = options.Value.Twilio;
        var endpoint = new Uri(
            settings.BaseUrl,
            $"/2010-04-01/Accounts/{Uri.EscapeDataString(settings.AccountSid!)}/Messages.json");
        var authorization = Authorization(settings);
        var form = Form(message, settings);

        using var response = await retry.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Headers = { Authorization = authorization },
                Content = new FormUrlEncodedContent(form),
            },
            "Twilio",
            logger,
            cancellationToken).ConfigureAwait(false);

        var result = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "SMS accepted by Twilio for {To} (message sid {Sid}, status {Status}).",
                Redact.Phone(message.To),
                result?.Sid,
                result?.Status);
            return;
        }

        var detail = result?.Message ?? response.ReasonPhrase ?? "no details";
        logger.LogError(
            "Twilio rejected SMS for {To}: HTTP {Status}, error {Code} {Detail}",
            Redact.Phone(message.To),
            (int)response.StatusCode,
            result?.Code,
            detail);
        throw new MessageDeliveryException(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Twilio refused the credentials. Check Sms:Twilio:AccountSid and the API key or auth token."
                : $"Twilio rejected the SMS (error {result?.Code}): {detail}",
            isTransient: false);
    }

    internal static AuthenticationHeaderValue Authorization(TwilioOptions settings)
    {
        var (user, password) = string.IsNullOrWhiteSpace(settings.ApiKeySid)
            ? (settings.AccountSid!, settings.AuthToken!)
            : (settings.ApiKeySid, settings.ApiKeySecret!);
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    internal static Dictionary<string, string> Form(SmsMessage message, TwilioOptions settings)
    {
        var form = new Dictionary<string, string>
        {
            ["To"] = message.To,
            ["Body"] = message.Body,
        };
        if (!string.IsNullOrWhiteSpace(settings.MessagingServiceSid))
        {
            form["MessagingServiceSid"] = settings.MessagingServiceSid;
        }
        else
        {
            form["From"] = settings.FromNumber!;
        }

        return form;
    }

    /// <summary>
    /// Reads the fields we use. Parsed by hand because <c>status</c> is a string ("queued") on success but a number
    /// (400) in error bodies.
    /// </summary>
    internal static async Task<TwilioResponse?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new TwilioResponse(
                Text(root, "sid"),
                Text(root, "status"),
                root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number) ? number : null,
                Text(root, "message"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}

/// <summary>The fields we use from Twilio's message resource or error body.</summary>
internal sealed record TwilioResponse(string? Sid, string? Status, int? Code, string? Message);
