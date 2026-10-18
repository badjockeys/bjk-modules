namespace BJK.Modules.Media.Domain;

public enum MediaType
{
    Video,
    Image,
}

/// <summary>Where a piece of media is in its journey. Only <see cref="Ready"/> media is ever shown to other users.</summary>
public enum MediaStatus
{
    /// <summary>An upload link has been issued; the file may not have arrived yet.</summary>
    Uploading,

    /// <summary>The original is in storage and a processing job has been queued.</summary>
    Processing,

    /// <summary>Processed files are published and can be shown.</summary>
    Ready,

    /// <summary>
    /// Processed and blurred, but automatic detection failed or wasn't sure, or a moderator added blur regions. The
    /// blurred files are kept privately until a moderator approves them (then <see cref="Ready"/>) or rejects them
    /// (then <see cref="Failed"/>). It can't be posted meanwhile.
    /// </summary>
    NeedsReview,

    /// <summary>The file was rejected or processing gave up after its retries.</summary>
    Failed,

    /// <summary>Removed by its owner or an admin. The row is kept; the files are deleted.</summary>
    Deleted,
}
