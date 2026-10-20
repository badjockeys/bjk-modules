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

    /// <summary>How often old notifications are cleared out. Every 6 hours by default.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);
}
