using BJK.Modules.Shared.Notifications;

namespace BJK.Modules.Notifications.Domain;

/// <summary>
/// Something for a user's bell, stored in <c>notifications.Notifications</c>. Reactions to the same post are grouped
/// into one notification while it's unread (<see cref="Count"/> people, the latest in <see cref="ActorId"/>).
/// Notifications older than <see cref="KeepFor"/> are deleted by the clean-up job.
/// </summary>
public sealed class Notification
{
    public const int PostTitleMaxLength = 120;

    /// <summary>How long notifications are kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    // For EF Core.
    private Notification()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Who it's for.</summary>
    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    /// <summary>Who did it (the latest, for grouped reactions). Null for system notifications.</summary>
    public Guid? ActorId { get; private set; }

    public Guid? PostId { get; private set; }

    public Guid? CommentId { get; private set; }

    /// <summary>The post's title when it happened.</summary>
    public string? PostTitle { get; private set; }

    /// <summary>A short sentence some types carry, such as a closed vote's result.</summary>
    public string? Detail { get; private set; }

    /// <summary>When it was last sent as a push notification (grouped reactions push at most hourly).</summary>
    public DateTimeOffset? LastPushedAt { get; private set; }

    /// <summary>How many people (1 unless grouped).</summary>
    public int Count { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it last changed (a grouped reaction added), for ordering.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static Notification Create(NotificationRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UserId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(request));
        }

        var title = request.PostTitle?.Trim();
        return new Notification
        {
            Id = Guid.CreateVersion7(now),
            UserId = request.UserId,
            Type = request.Type,
            ActorId = request.ActorId,
            PostId = request.PostId,
            CommentId = request.CommentId,
            PostTitle = string.IsNullOrEmpty(title) ? null : title.Length <= PostTitleMaxLength ? title : title[..PostTitleMaxLength],
            Detail = Trimmed(request.Detail, NotificationRequest.DetailMaxLength),
            Count = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Someone else did the same (another reaction to the same post): one more, and it's new again.</summary>
    public void AddOne(Guid? actorId, DateTimeOffset now)
    {
        Count++;
        ActorId = actorId ?? ActorId;
        UpdatedAt = now;
    }

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    /// <summary>True if it may be pushed again: never pushed, or last pushed over an hour ago.</summary>
    public bool MayPush(DateTimeOffset now) => LastPushedAt is not { } last || now - last >= TimeSpan.FromHours(1);

    public void MarkPushed(DateTimeOffset now) => LastPushedAt = now;

    private static string? Trimmed(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
    }
}

/// <summary>
/// One person's choice for one type of notification, stored in <c>notifications.NotificationSettings</c> only when it
/// differs from the default (<see cref="NotificationDefaults"/>).
/// </summary>
public sealed class NotificationSetting
{
    // For EF Core.
    private NotificationSetting()
    {
    }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    /// <summary>Show it in the bell.</summary>
    public bool InApp { get; private set; }

    /// <summary>Also send an email.</summary>
    public bool Email { get; private set; }

    /// <summary>Also send a push notification to the devices where they turned push on.</summary>
    public bool Push { get; private set; } = true;

    public static NotificationSetting Create(Guid userId, NotificationType type, bool inApp, bool email, bool push = true) =>
        new() { UserId = userId, Type = type, InApp = inApp, Email = email, Push = push };

    public void Change(bool inApp, bool email, bool push)
    {
        InApp = inApp;
        Email = email;
        Push = push;
    }
}

/// <summary>
/// Where to email someone: their confirmed address, as their own token last said. Recorded when they check their
/// notifications (the bell), so it follows changes in the identity server, and removed if it's no longer confirmed.
/// </summary>
public sealed class NotificationContact
{
    public const int EmailMaxLength = 254;

    // For EF Core.
    private NotificationContact()
    {
    }

    public Guid UserId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationContact Create(Guid userId, string email, DateTimeOffset now) =>
        new() { UserId = userId, Email = Checked(email), UpdatedAt = now };

    /// <summary>Returns true if the address changed.</summary>
    public bool Change(string email, DateTimeOffset now)
    {
        var value = Checked(email);
        if (value == Email)
        {
            return false;
        }

        Email = value;
        UpdatedAt = now;
        return true;
    }

    private static string Checked(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var value = email.Trim();
        return value.Length <= EmailMaxLength ? value : throw new ArgumentException("Email address too long.", nameof(email));
    }
}

/// <summary>
/// What each type does when someone hasn't chosen: everything in the bell and as a push (on devices where they turned
/// push on); email only for what needs them.
/// </summary>
public static class NotificationDefaults
{
    /// <summary>The bell, email and push defaults for a type.</summary>
    public static (bool InApp, bool Email, bool Push) For(NotificationType type) => type switch
    {
        NotificationType.PassengerTagged => (true, true, true),
        _ => (true, false, true),
    };

    /// <summary>Types a person can change, in the order the settings page lists them.</summary>
    public static readonly IReadOnlyList<NotificationType> Configurable =
    [
        NotificationType.PassengerTagged,
        NotificationType.PassengerAccepted,
        NotificationType.CommentOnPost,
        NotificationType.ReplyToComment,
        NotificationType.ReactionOnPost,
        NotificationType.NewFollower,
        NotificationType.CommentOnFollowedPost,
        NotificationType.VoteClosed,
    ];
}
