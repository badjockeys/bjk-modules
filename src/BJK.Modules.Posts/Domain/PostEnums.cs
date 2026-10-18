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
}

public enum ReportTargetType
{
    Post,
    Comment,
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
