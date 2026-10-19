namespace BJK.Modules.Media.Domain;

/// <summary>
/// Marks a piece of media as sensitive (it's in a post tagged sensitive: graphic injuries, serious crashes). Stored in
/// <c>media.SensitiveMedia</c>, one row per sensitive media, separate from <see cref="MediaItem"/> so that tagging a
/// post never clashes with a worker that is processing its media at the same moment. This is what should be true; the
/// media's <see cref="MediaItem.IsRestricted"/> is where its files actually are, and the worker brings the two into line.
/// </summary>
public sealed class SensitiveMediaMark
{
    // For EF Core.
    private SensitiveMediaMark()
    {
    }

    /// <param name="mediaId">The media.</param>
    /// <param name="markedAt">When it was marked.</param>
    /// <param name="source">Who marked it.</param>
    /// <param name="score">For automatic marks: the graphic content score.</param>
    public SensitiveMediaMark(Guid mediaId, DateTimeOffset markedAt, SensitiveMarkSource source = SensitiveMarkSource.Post, double? score = null)
    {
        MediaId = mediaId;
        MarkedAt = markedAt;
        Source = source;
        Score = score;
    }

    public Guid MediaId { get; private set; }

    public DateTimeOffset MarkedAt { get; private set; }

    /// <summary>Who marked it first: its post's sensitive tag, or automatic detection in the media worker.</summary>
    public SensitiveMarkSource Source { get; private set; }

    /// <summary>For automatic marks: the graphic content score that crossed the threshold.</summary>
    public double? Score { get; private set; }
}

/// <summary>Where a sensitive mark came from.</summary>
public enum SensitiveMarkSource
{
    /// <summary>The post was tagged sensitive (by its author or a moderator), through the Posts module.</summary>
    Post,

    /// <summary>The media worker's graphic content classifier scored it at or above the threshold.</summary>
    Automatic,
}
