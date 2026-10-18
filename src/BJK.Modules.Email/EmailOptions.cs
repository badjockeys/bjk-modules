namespace BJK.Modules.Email;

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
