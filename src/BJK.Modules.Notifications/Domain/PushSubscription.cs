using System.Buffers.Text;

namespace BJK.Modules.Notifications.Domain;

/// <summary>
/// One browser or phone where someone turned push notifications on, stored in <c>notifications.PushSubscriptions</c>.
/// <c>Endpoint</c> is the push service's address for that browser; <c>P256dh</c> and <c>Auth</c> are the browser's keys
/// (base64url) that messages are encrypted to (RFC 8291). A browser belongs to whoever subscribed it last. Removed when
/// they turn push off, when the push service says it's gone, or with their account.
/// </summary>
public sealed class PushSubscription
{
    /// <summary>Longest endpoint kept (they're about 200–500 characters; the unique index allows up to 850).</summary>
    public const int EndpointMaxLength = 800;

    public const int KeyMaxLength = 100;

    /// <summary>Devices per person; subscribing another replaces their oldest.</summary>
    public const int MaxPerUser = 10;

    // For EF Core.
    private PushSubscription()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>The browser's public key (P-256, uncompressed, base64url).</summary>
    public string P256dh { get; private set; } = string.Empty;

    /// <summary>The browser's 16-byte authentication secret (base64url).</summary>
    public string Auth { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PushSubscription Create(Guid userId, string endpoint, string p256dh, string auth, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>The same browser subscribed again (new keys, or now signed in as someone else).</summary>
    public void Renew(Guid userId, string p256dh, string auth, DateTimeOffset now)
    {
        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
        UpdatedAt = now;
    }
}

/// <summary>
/// The push services browsers use. Messages are only ever sent to these, so a made-up endpoint can't make the server
/// call somewhere else.
/// </summary>
public static class PushServices
{
    private static readonly string[] Hosts = ["fcm.googleapis.com", "updates.push.services.mozilla.com"];

    private static readonly string[] Domains = [".push.apple.com", ".notify.windows.com", ".push.services.mozilla.com"];

    /// <summary>True for an https address at a known push service, on the default port.</summary>
    public static bool IsAllowed(string? endpoint) =>
        endpoint is { Length: > 0 and <= PushSubscription.EndpointMaxLength }
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || Domains.Any(d => uri.Host.EndsWith(d, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True for a P-256 public key: 65 bytes, uncompressed (starts 0x04), base64url.</summary>
    public static bool IsPublicKey(string? value) => DecodedLength(value, out var first) == 65 && first == 0x04;

    /// <summary>True for a 16-byte authentication secret, base64url.</summary>
    public static bool IsAuthSecret(string? value) => DecodedLength(value, out _) == 16;

    private static int DecodedLength(string? value, out byte first)
    {
        first = 0;
        if (string.IsNullOrEmpty(value) || value.Length > PushSubscription.KeyMaxLength)
        {
            return -1;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            first = bytes.Length > 0 ? bytes[0] : (byte)0;
            return bytes.Length;
        }
        catch (FormatException)
        {
            return -1;
        }
    }
}

/// <summary>
/// No push notifications overnight: 22:00 to 07:00 UK time, for everyone (owner, 21 Oct 2026). They still go in the
/// bell; they just don't buzz a phone.
/// </summary>
public static class PushQuietHours
{
    public const int StartHour = 22;

    public const int EndHour = 7;

    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException("The Europe/London time zone isn't available (install tzdata in the container).");
    });

    /// <summary>True between 22:00 and 07:00 in the UK.</summary>
    public static bool IsQuiet(DateTimeOffset now)
    {
        var hour = TimeZoneInfo.ConvertTime(now, Zone.Value).Hour;
        return hour >= StartHour || hour < EndHour;
    }
}
