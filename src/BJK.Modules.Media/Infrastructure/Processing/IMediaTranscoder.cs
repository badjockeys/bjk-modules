namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>What ffprobe found in a file.</summary>
/// <param name="HasVideo">False if the file has no picture at all (not a video or photo).</param>
/// <param name="DurationSeconds">Null for photos.</param>
/// <param name="Width">Width as seen, after any rotation.</param>
/// <param name="Height">Height as seen, after any rotation.</param>
/// <param name="FrameRate">Average frames per second of a video, if known.</param>
/// <param name="StartSeconds">When the video stream starts in the file's timeline (usually 0), if known.</param>
public sealed record ProbeResult(bool HasVideo, decimal? DurationSeconds, int? Width, int? Height, double? FrameRate = null, double? StartSeconds = null);

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

    /// <summary>
    /// Writes the heavily blurred, low-resolution WebP preview shown in place of sensitive media: the picture is shrunk
    /// to a few dozen pixels (destroying detail), blurred, enlarged and blurred again, without metadata. The input is an
    /// already blurred thumbnail (JPEG or WebP).
    /// </summary>
    /// <param name="inputPath">The thumbnail.</param>
    /// <param name="previewPath">Where to write the preview.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    Task CreateSensitivePreviewAsync(string inputPath, string previewPath, CancellationToken cancellationToken);

    /// <summary>
    /// Joins blurred video segments, in order, into one MP4 without re-encoding (ffmpeg's concat demuxer; every segment
    /// is encoded with identical settings and starts on a keyframe), with fast start and no metadata. If
    /// <paramref name="audioSourcePath"/> is given (audio removal switched off), the original's soundtrack is added.
    /// </summary>
    /// <param name="segmentPaths">The segment files, in order.</param>
    /// <param name="audioSourcePath">The original, to take sound from; null for no sound.</param>
    /// <param name="outputPath">Where to write the joined MP4.</param>
    /// <param name="cancellationToken">Stops the job.</param>
    Task ConcatenateVideoAsync(IReadOnlyList<string> segmentPaths, string? audioSourcePath, string outputPath, CancellationToken cancellationToken);
}

/// <summary>The file can't be processed whatever we do: corrupt, or not a video or photo.</summary>
public sealed class UnreadableMediaException(string message, Exception? innerException = null)
    : Exception(message, innerException);
