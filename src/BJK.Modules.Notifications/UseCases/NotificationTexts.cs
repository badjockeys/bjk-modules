using BJK.Modules.Shared.Notifications;

namespace BJK.Modules.Notifications.UseCases;

/// <summary>The words for each notification, in the bell and in emails.</summary>
public static class NotificationTexts
{
    /// <summary>"Sam J commented on your post “Tailgated on the A57”."</summary>
    /// <param name="type">What happened.</param>
    /// <param name="actorName">Who did it, or null if they've gone ("Someone").</param>
    /// <param name="count">How many people, for grouped reactions.</param>
    /// <param name="postTitle">The post's title, if any.</param>
    public static string Sentence(NotificationType type, string? actorName, int count, string? postTitle)
    {
        var who = string.IsNullOrWhiteSpace(actorName) ? "Someone" : actorName;
        var post = string.IsNullOrWhiteSpace(postTitle) ? "your post" : $"your post “{postTitle}”";
        var on = string.IsNullOrWhiteSpace(postTitle) ? "a post" : $"“{postTitle}”";
        return type switch
        {
            NotificationType.PassengerTagged => $"{who} tagged you as a passenger on {on}. Accept to show your name.",
            NotificationType.PassengerAccepted => $"{who} accepted your passenger tag on {on}.",
            NotificationType.CommentOnPost => $"{who} commented on {post}.",
            NotificationType.ReplyToComment => $"{who} replied to your comment on {on}.",
            NotificationType.ReactionOnPost when count > 1 =>
                $"{who} and {count - 1} {(count == 2 ? "other" : "others")} reacted to {post}.",
            NotificationType.ReactionOnPost => $"{who} reacted to {post}.",
            NotificationType.AreaPost when count > 1 =>
                $"{count} new posts in your area, including {on}.",
            NotificationType.AreaPost => $"New in your area: {on}.",
            NotificationType.NewFollower => $"{who} followed you.",
            _ => "You have a new notification.",
        };
    }

    /// <summary>Email subject lines.</summary>
    public static string Subject(NotificationType type, string? actorName)
    {
        var who = string.IsNullOrWhiteSpace(actorName) ? "Someone" : actorName;
        return type switch
        {
            NotificationType.PassengerTagged => $"{who} tagged you as a passenger on Bad Jockeys",
            NotificationType.PassengerAccepted => $"{who} accepted your passenger tag",
            NotificationType.CommentOnPost => $"{who} commented on your post",
            NotificationType.ReplyToComment => $"{who} replied to your comment",
            NotificationType.ReactionOnPost => $"{who} reacted to your post",
            NotificationType.AreaPost => "New bad driving reported in your area",
            NotificationType.NewFollower => $"{who} followed you on Bad Jockeys",
            _ => "New on Bad Jockeys",
        };
    }

    /// <summary>The label on the settings page.</summary>
    public static string Label(NotificationType type) => type switch
    {
        NotificationType.PassengerTagged => "Someone tags you as a passenger",
        NotificationType.PassengerAccepted => "Someone accepts your passenger tag",
        NotificationType.CommentOnPost => "Comments on your posts",
        NotificationType.ReplyToComment => "Replies to your comments",
        NotificationType.ReactionOnPost => "Reactions to your posts",
        NotificationType.AreaPost => "New posts in your area",
        NotificationType.NewFollower => "New followers",
        _ => type.ToString(),
    };
}
