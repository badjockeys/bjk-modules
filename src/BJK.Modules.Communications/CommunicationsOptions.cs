namespace BJK.Modules.Communications;

/// <summary>Email settings. Section <c>Email</c>. Secrets (the API key) go in user secrets locally and Key Vault in Azure.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public const string LogProvider = "Log";
    public const string SendGridProvider = "SendGrid";

    /// <summary>
    /// <c>Log</c> (default): nothing is sent. In Development the whole email is written to the log so flows can be
    /// tested; elsewhere only the fact that it was dropped is logged. <c>SendGrid</c>: sent through SendGrid.
    /// </summary>
    public string Provider { get; set; } = LogProvider;

    /// <summary>Sender address, e.g. no-reply@badjockeys.com. Must be a verified sender or domain in SendGrid.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Bad Jockeys";

    /// <summary>Optional Reply-To address, e.g. support@badjockeys.com.</summary>
    public string? ReplyToAddress { get; set; }

    /// <summary>
    /// Absolute URL of a PNG logo shown at the top of branded emails, e.g. https://badjockeys.com/email-logo.png.
    /// Use PNG: Gmail and Outlook don't show SVG. If not set, the emails have a text header instead.
    /// </summary>
    public Uri? LogoUrl { get; set; }

    public SendGridOptions SendGrid { get; set; } = new();
}

/// <summary>Section <c>Email:SendGrid</c>.</summary>
public sealed class SendGridOptions
{
    /// <summary>Secret. An API key with the "Mail Send" permission only.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// API address. <c>https://api.sendgrid.com</c> by default; use <c>https://api.eu.sendgrid.com</c> for an
    /// EU-pinned subuser.
    /// </summary>
    public Uri BaseUrl { get; set; } = new("https://api.sendgrid.com");

    /// <summary>
    /// True: SendGrid validates each request but delivers nothing. Useful for staging and CI against the real API.
    /// </summary>
    public bool SandboxMode { get; set; }
}

/// <summary>SMS settings. Section <c>Sms</c>.</summary>
public sealed class SmsOptions
{
    public const string SectionName = "Sms";

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
/// Section <c>Sms:Twilio</c>. Authenticate with an API key (<see cref="ApiKeySid"/> and <see cref="ApiKeySecret"/>,
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
