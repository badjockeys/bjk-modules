namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>What ffprobe found in a file.</summary>
/// <param name="HasVideo">False if the file has no picture at all (not a video or photo).</param>
/// <param name="DurationSeconds">Null for photos.</param>
/// <param name="Width">Width as seen, after any rotation.</param>
/// <param name="Height">Height as seen, after any rotation.</param>
/// <param name="FrameRate">Average frames per second of a video, if known.</param>
public sealed record ProbeResult(bool HasVideo, decimal? DurationSeconds, int? Width, int? Height, double? FrameRate = null);

/// <summary>Converts originals into the files we publish. Implemented with ffmpeg.</summary>
public interface IMediaTranscoder
{
    /// <exception cref="UnreadableMediaException">The file isn't a video or photo ffmpeg can read.</exception>
    Task<ProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a JPEG thumbnail from a frame one second into an already blurred playback video (or its first frame if
    /// it's shorter). The encoding itself is done by <see cref="Redaction.IMediaRedactor"/>, so nothing unblurred is
    /// ever produced.
    /// </summary>
    Task CreateVideoThumbnailAsync(string playbackPath, string thumbnailPath, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a WebP photo and a WebP thumbnail, both without metadata (EXIF, GPS). The input is the blurred picture
    /// from <see cref="Redaction.IMediaRedactor"/>.
    /// </summary>
    Task ConvertImageAsync(string inputPath, string imagePath, string thumbnailPath, CancellationToken cancellationToken);
}

/// <summary>The file can't be processed whatever we do: corrupt, or not a video or photo.</summary>
public sealed class UnreadableMediaException(string message, Exception? innerException = null)
    : Exception(message, innerException);
