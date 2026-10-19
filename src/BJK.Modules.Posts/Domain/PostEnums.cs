namespace BJK.Modules.Posts.Domain;

/// <summary>What kind of bad driving a post shows. Stored by name, so new values can be added safely.</summary>
public enum IncidentCategory
{
    RoadRage,
    DangerousOvertaking,
    Tailgating,
    MobilePhoneUse,
    Speeding,
    RedLightRunning,
    CuttingUp,
    Undertaking,
    LaneHogging,
    ClosePass,
    BadParking,
    Other,
}

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

public enum ReactionType
{
    Like,
    Angry,
    Shocked,
    Laugh,
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
