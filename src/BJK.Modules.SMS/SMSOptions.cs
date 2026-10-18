namespace BJK.Modules.SMS;

/// <summary>SMS settings. Section <c>SMS</c>.</summary>
public sealed class SMSOptions
{
    public const string SectionName = "SMS";

    public const string LogProvider = "Log";
    public const string TwilioProvider = "Twilio";

    /// <summary>
    /// <c>Log</c> (default): nothing is sent; in Development the message is written to the log.
    /// <c>Twilio</c>: sent through Twilio.
    /// </summary>
    public string Provider { get; set; } = LogProvider;

    public TwilioOptions Twilio { get; set; } = new();
}

/// <summary>
/// Section <c>SMS:Twilio</c>. Authenticate with an API key (<see cref="ApiKeySid"/> and <see cref="ApiKeySecret"/>,
/// recommended) or the account's <see cref="AuthToken"/>. Send from a Messaging Service (recommended) or one number.
/// </summary>
public sealed class TwilioOptions
{
    /// <summary>Account SID, starting AC.</summary>
    public string? AccountSid { get; set; }

    /// <summary>API key SID, starting SK. Preferred over the auth token because it can be revoked on its own.</summary>
    public string? ApiKeySid { get; set; }

    /// <summary>Secret for <see cref="ApiKeySid"/>.</summary>
    public string? ApiKeySecret { get; set; }

    /// <summary>Secret. The account auth token; only used when no API key is set.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Messaging Service SID, starting MG. Lets Twilio pick the sender (number pool, alphanumeric ID).</summary>
    public string? MessagingServiceSid { get; set; }

    /// <summary>Sender number in E.164 format, or an alphanumeric sender ID such as BADJOCKEYS. Used when no Messaging Service is set.</summary>
    public string? FromNumber { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.twilio.com");
}
