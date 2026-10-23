namespace BJK.Modules.Posts.Domain;

public enum PostStatus
{
    /// <summary>Visible to everyone.</summary>
    Published,

    /// <summary>Hidden by an admin, usually after a report. Its author and admins can still see it.</summary>
    Hidden,

    /// <summary>
    /// Content screening scored the text as likely abusive. Only its author and moderators see it until a moderator
    /// approves or removes it.
    /// </summary>
    HeldForReview,

    /// <summary>Removed by its author or an admin. The row is kept; its media files are deleted.</summary>
    Deleted,
}

/// <summary>
/// Whether a post's photos and video are ready to show. A post can be created while its media are still being
/// processed (or waiting for a moderator to check the blurring); it's then <see cref="Pending"/>, seen only by its
/// author and moderators, and is published automatically once every item is Ready. Separate from
/// <see cref="PostStatus"/>, which is about moderation: a post is visible to others only when it's Published and its
/// media are Ready.
/// </summary>
public enum PostMediaState
{
    /// <summary>Every photo or video is ready: the post shows to others if its status allows.</summary>
    Ready,

    /// <summary>Waiting for media processing (or review). Only the author and moderators see the post.</summary>
    Pending,

    /// <summary>
    /// A photo or video failed processing, was rejected by a moderator, or was deleted, so the post won't be published.
    /// The author sees why and was emailed. They can delete the post and try again.
    /// </summary>
    Failed,
}

/// <summary>Who set a post's sensitive tag. Stored by name.</summary>
public enum PostSensitiveSource
{
    /// <summary>Not tagged.</summary>
    None,

    /// <summary>The author ticked "sensitive".</summary>
    Author,

    /// <summary>A moderator tagged it.</summary>
    Moderator,

    /// <summary>Automatic graphic content detection in the media worker tagged it; a moderator confirms or undoes it.</summary>
    Automatic,
}

/// <summary>Where an automatic sensitive tag is in moderator review. Stored by name.</summary>
public enum SensitiveReviewStatus
{
    /// <summary>No automatic tag to review.</summary>
    None,

    /// <summary>Tagged automatically; waiting in the moderators' sensitive-review queue.</summary>
    Pending,

    /// <summary>A moderator agreed the post is sensitive.</summary>
    Confirmed,

    /// <summary>A moderator removed the automatic tag. Detection won't tag the post again.</summary>
    Undone,
}

/// <summary>
/// The reactions, stored by name. Heart replaced Laugh on 22 Oct 2026 (existing Laugh reactions became Like).
/// </summary>
public enum ReactionType
{
    Like,
    Heart,
    Angry,
    Shocked,
}

public enum CommentStatus
{
    Visible,
    Hidden,
    Deleted,

    /// <summary>Held by content screening: only its author and moderators see it until a moderator decides.</summary>
    HeldForReview,
}

public enum ReportTargetType
{
    Post,
    Comment,

    /// <summary>A user's public profile (handle, display name, bio, avatar).</summary>
    Profile,

    /// <summary>A community: its name, description or rules.</summary>
    Community,

    /// <summary>A discussion in a community.</summary>
    CommunityDiscussion,

    /// <summary>A reply to a discussion in a community.</summary>
    CommunityReply,
}

public enum ReportReason
{
    /// <summary>Shows or encourages something dangerous or illegal beyond the incident itself.</summary>
    DangerousOrIllegal,

    /// <summary>Shows personal details, such as a face, a home or private information.</summary>
    PersonalInformation,

    HarassmentOrHate,

    Spam,

    NotADrivingIncident,

    Other,

    /// <summary>Illegal content, such as child abuse imagery or terrorist material. Urgent.</summary>
    IllegalContent,

    /// <summary>A threat to someone's safety. Urgent.</summary>
    Threat,

    /// <summary>"I'm in this clip": the reporter is identifiable in the post.</summary>
    InThisClip,

    /// <summary>The reporter owns the copyright in the photos or video.</summary>
    Copyright,

    /// <summary>The account pretends to be someone else. Accounts only.</summary>
    Impersonation,

    /// <summary>The account belongs to someone under 13 (too young for Bad Jockeys). Accounts only.</summary>
    UnderThirteen,
}

/// <summary>Report reasons in words, and which are only for accounts.</summary>
public static class ReportReasons
{
    /// <summary>Reasons only an account (profile) can be reported for.</summary>
    public static readonly IReadOnlySet<ReportReason> AccountOnly = new HashSet<ReportReason>
    {
        ReportReason.Impersonation,
        ReportReason.UnderThirteen,
    };

    /// <summary>The reason as people would say it, for emails.</summary>
    public static string Describe(ReportReason reason) => reason switch
    {
        ReportReason.DangerousOrIllegal => "dangerous or illegal",
        ReportReason.PersonalInformation => "personal information",
        ReportReason.HarassmentOrHate => "harassment or hate",
        ReportReason.Spam => "spam",
        ReportReason.NotADrivingIncident => "not a driving incident",
        ReportReason.IllegalContent => "illegal content",
        ReportReason.Threat => "a threat",
        ReportReason.InThisClip => "I'm in this clip",
        ReportReason.Copyright => "copyright",
        ReportReason.Impersonation => "impersonation",
        ReportReason.UnderThirteen => "under 13",
        _ => "something else",
    };
}

/// <summary>Who can comment on a post, chosen by its author. Stored by name.</summary>
public enum CommentSetting
{
    /// <summary>Anyone who can comment (the default).</summary>
    Everyone,

    /// <summary>Nobody can add comments or replies; the ones already there stay.</summary>
    Off,

    /// <summary>Only people the post's author follows (and the author) can add comments and replies.</summary>
    Following,
}

/// <summary>How quickly a report needs a decision; see <see cref="Report.UrgentResponseTime"/>.</summary>
public enum ReportPriority
{
    Normal,

    /// <summary>Illegal content and threats: looked at first, with a target of hours rather than days.</summary>
    Urgent,
}

/// <summary>Where a report came from.</summary>
public enum ReportSource
{
    /// <summary>A signed-in user, in the app.</summary>
    App,

    /// <summary>The public "Report content / I'm in this clip" web form, by someone without an account.</summary>
    PublicForm,
}

public enum AppealStatus
{
    Open,

    /// <summary>A moderator looked again and the original decision stands.</summary>
    Upheld,

    /// <summary>A moderator reversed the decision: the content is visible again.</summary>
    Overturned,
}

/// <summary>A moderator's decision on an appeal.</summary>
public enum AppealDecision
{
    Uphold,
    Overturn,
}

/// <summary>The content screening queues moderators work through.</summary>
public enum ScreeningQueue
{
    /// <summary>Held for review: not visible to others until approved.</summary>
    Held,

    /// <summary>Published but flagged.</summary>
    Flagged,
}

public enum ReportStatus
{
    Open,

    /// <summary>An admin looked and left the content up.</summary>
    Dismissed,

    /// <summary>An admin hid the content.</summary>
    ContentHidden,
}

/// <summary>What an admin decides about a report.</summary>
public enum ReportAction
{
    Dismiss,
    HideContent,
}
