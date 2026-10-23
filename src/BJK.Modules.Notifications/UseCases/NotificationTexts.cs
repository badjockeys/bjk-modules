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
    /// <param name="detail">The notification's extra sentence, if it has one (a closed vote's result; for a community
    /// reply, the community's name).</param>
    public static string Sentence(NotificationType type, string? actorName, int count, string? postTitle, string? detail = null)
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
            NotificationType.CommentOnFollowedPost => $"{who} commented on {on}, which you follow.",
            NotificationType.VoteClosed => string.IsNullOrWhiteSpace(detail)
                ? $"The vote on {on} has closed."
                : $"The vote on {on} has closed. {detail}",
            NotificationType.CommunityJoinRequest => $"{who} asked to join {Community(postTitle)}.",
            NotificationType.CommunityRequestApproved => $"You're in: your request to join {Community(postTitle)} was approved.",
            NotificationType.CommunityInvite => $"{who} invited you to join {Community(postTitle)}.",
            NotificationType.CommunityReply => string.IsNullOrWhiteSpace(detail)
                ? $"{who} replied to your discussion {on}."
                : $"{who} replied to your discussion {on} in {detail}.",
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
            NotificationType.CommentOnFollowedPost => $"{who} commented on a post you follow",
            NotificationType.VoteClosed => "The vote has closed: see the result",
            NotificationType.CommunityJoinRequest => $"{who} asked to join your community",
            NotificationType.CommunityRequestApproved => "You're in: your request to join was approved",
            NotificationType.CommunityInvite => $"{who} invited you to a community on Bad Jockeys",
            NotificationType.CommunityReply => $"{who} replied to your discussion",
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
        NotificationType.CommentOnFollowedPost => "Comments on posts you follow",
        NotificationType.VoteClosed => "Vote results",
        NotificationType.CommunityJoinRequest => "Requests to join communities you run",
        NotificationType.CommunityRequestApproved => "Your requests to join are approved",
        NotificationType.CommunityInvite => "Invitations to communities",
        NotificationType.CommunityReply => "Replies to your community discussions",
        _ => type.ToString(),
    };

    private static string Community(string? name) => string.IsNullOrWhiteSpace(name) ? "a community" : name;
}
