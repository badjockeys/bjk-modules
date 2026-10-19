namespace BJK.Modules.Media.Domain;

/// <summary>
/// A blur region a moderator adds by hand, for something automatic detection missed. Positions and sizes are
/// fractions of the picture (0 to 1 from the top left), so they apply at any resolution. For videos, an optional
/// time range limits it to part of the clip; without one it covers the whole clip.
/// </summary>
/// <param name="X">Left edge, as a fraction of the width.</param>
/// <param name="Y">Top edge, as a fraction of the height.</param>
/// <param name="Width">Width, as a fraction of the picture's width.</param>
/// <param name="Height">Height, as a fraction of the picture's height.</param>
/// <param name="StartSeconds">Videos: when the region starts. Null means from the beginning.</param>
/// <param name="EndSeconds">Videos: when the region ends. Null means to the end.</param>
public sealed record ManualRedaction(
    double X,
    double Y,
    double Width,
    double Height,
    double? StartSeconds = null,
    double? EndSeconds = null)
{
    /// <summary>Most regions one piece of media may have.</summary>
    public const int MaxPerMedia = 50;

    /// <summary>True if the region applies at <paramref name="seconds"/> into the clip (always true for photos).</summary>
    /// <param name="seconds">Time of the frame, or null for a photo.</param>
    public bool AppliesAt(double? seconds) =>
        seconds is not { } t || ((StartSeconds is null || t >= StartSeconds) && (EndSeconds is null || t <= EndSeconds));
}
