namespace BJK.Modules.Notifications;

/// <summary>Notification settings. Section <c>Notifications</c> (user secrets locally, app settings in Azure).</summary>
public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// The web app's address, e.g. https://badjockeys.com, for the links in notification emails. Without it, emails
    /// say what happened but have no link.
    /// </summary>
    public Uri? AppUrl { get; set; }

    /// <summary>The place lookup (postcodes.io). Ends with a slash.</summary>
    public Uri PlacesBaseUrl { get; set; } = new("https://api.postcodes.io/");

    /// <summary>How often old notifications are cleared out. Every 6 hours by default.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Web push (section <c>Notifications:Push</c>). Without keys, push is off and the app doesn't offer it.</summary>
    public PushOptions Push { get; set; } = new();
}

/// <summary>
/// The VAPID keys (RFC 8292) that identify this server to the browsers' push services. Generate a pair once per
/// environment (the bjk-core setup does it) and keep them: changing them stops every existing subscription working.
/// </summary>
public sealed class PushOptions
{
    /// <summary>The public key: a P-256 point, uncompressed, base64url (87 characters). The web app subscribes with it.</summary>
    public string? PublicKey { get; set; }

    /// <summary>The private key: the 32-byte P-256 scalar, base64url (43 characters). A secret.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Who push services can contact about this server: <c>mailto:</c> an address, or an https page.</summary>
    public string Subject { get; set; } = "mailto:support@badjockeys.com";

    /// <summary>How long a push service keeps trying to deliver a message to an offline device.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(12);

    /// <summary>True when both keys are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
