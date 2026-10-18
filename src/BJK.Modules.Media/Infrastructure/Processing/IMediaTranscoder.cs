namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>What ffprobe found in a file.</summary>
/// <param name="HasVideo">False if the file has no picture at all (not a video or photo).</param>
/// <param name="DurationSeconds">Null for photos.</param>
/// <param name="Width">Width as seen, after any rotation.</param>
/// <param name="Height">Height as seen, after any rotation.</param>
public sealed record ProbeResult(bool HasVideo, decimal? DurationSeconds, int? Width, int? Height);

/// <summary>Converts originals into the files we publish. Implemented with ffmpeg.</summary>
public interface IMediaTranscoder
{
    /// <exception cref="UnreadableMediaException">The file isn't a video or photo ffmpeg can read.</exception>
    Task<ProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Writes an H.264/AAC MP4 for web playback (no metadata, so no GPS or device details) and a JPEG thumbnail.
    /// </summary>
    Task TranscodeVideoAsync(string inputPath, string playbackPath, string thumbnailPath, CancellationToken cancellationToken);

    /// <summary>Writes a WebP photo and a WebP thumbnail, both without metadata (EXIF, GPS).</summary>
    Task ConvertImageAsync(string inputPath, string imagePath, string thumbnailPath, CancellationToken cancellationToken);
}

/// <summary>The file can't be processed whatever we do: corrupt, or not a video or photo.</summary>
public sealed class UnreadableMediaException(string message, Exception? innerException = null)
    : Exception(message, innerException);
