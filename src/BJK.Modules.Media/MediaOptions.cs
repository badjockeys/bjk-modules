using System.ComponentModel.DataAnnotations;

namespace BJK.Modules.Media;

/// <summary>Limits and timings for uploads. Section <c>Media</c>; every value has a sensible default.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    [Range(1, long.MaxValue)]
    public long MaxVideoBytes { get; set; } = 500L * 1024 * 1024;

    [Range(1, long.MaxValue)]
    public long MaxImageBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Checked by the worker once ffmpeg has read the video.</summary>
    [Range(1, int.MaxValue)]
    public int MaxVideoDurationSeconds { get; set; } = 300;

    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long originals are kept. Must match the lifecycle rule on the originals bucket.</summary>
    public TimeSpan OriginalRetention { get; set; } = TimeSpan.FromDays(90);

    public long MaxBytesFor(Domain.MediaType type) =>
        type == Domain.MediaType.Video ? MaxVideoBytes : MaxImageBytes;
}

/// <summary>Cloudflare R2 (or any S3-compatible store). Section <c>Media:Storage</c>.</summary>
public sealed class MediaStorageOptions
{
    public const string SectionName = "Media:Storage";

    /// <summary>e.g. https://&lt;account id&gt;.r2.cloudflarestorage.com</summary>
    [Required]
    public Uri? ServiceUrl { get; set; }

    [Required]
    public string? AccessKeyId { get; set; }

    /// <summary>Secret: user secrets locally, Key Vault in Azure.</summary>
    [Required]
    public string? SecretAccessKey { get; set; }

    /// <summary>Private bucket for uploaded originals.</summary>
    [Required]
    public string? OriginalsBucket { get; set; }

    /// <summary>Public bucket for processed files.</summary>
    [Required]
    public string? PublicBucket { get; set; }

    /// <summary>Address the public bucket is served from through Cloudflare, e.g. https://media.badjockeys.com/</summary>
    [Required]
    public Uri? PublicBaseUrl { get; set; }
}

/// <summary>The processing queue. Section <c>Media:Queue</c>. The RabbitMQ connection itself is <c>Messaging:ConnectionUri</c>.</summary>
public sealed class MediaQueueOptions
{
    public const string SectionName = "Media:Queue";

    [Required]
    public string QueueName { get; set; } = "media.process";
}

/// <summary>The processing worker. Section <c>Media:Processing</c>; only the worker host reads it.</summary>
public sealed class MediaProcessingOptions
{
    public const string SectionName = "Media:Processing";

    /// <summary>ffmpeg executable: a name on the PATH or a full path.</summary>
    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>ffprobe executable: a name on the PATH or a full path.</summary>
    [Required]
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Where each job's temporary files go; each job gets its own folder, removed when it finishes.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Jobs one worker runs at once. Video encoding uses every core, so 1 is usually right.</summary>
    [Range(1, 16)]
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>Height of the playback video. Smaller videos keep their size.</summary>
    [Range(240, 2160)]
    public int VideoHeight { get; set; } = 720;

    /// <summary>x264 quality (lower is better and bigger). 23 is a good balance for dashcam footage.</summary>
    [Range(15, 35)]
    public int VideoCrf { get; set; } = 23;

    /// <summary>Longest side of processed photos, in pixels.</summary>
    [Range(320, 8192)]
    public int ImageMaxSize { get; set; } = 2048;

    /// <summary>Longest side of thumbnails, in pixels.</summary>
    [Range(64, 1920)]
    public int ThumbnailMaxSize { get; set; } = 640;

    /// <summary>Gives up on one ffmpeg run after this long.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Automatic blurring of faces and number plates. Section <c>Media:Redaction</c>; only the worker host reads it.
/// If a model is missing or fails, or a detection is uncertain, the media goes to <c>NeedsReview</c> instead of
/// <c>Ready</c>, so nothing unchecked is published.
/// </summary>
public sealed class MediaRedactionOptions
{
    public const string SectionName = "Media:Redaction";

    /// <summary>
    /// The YuNet face detection model (ONNX), e.g. <c>/opt/bjk/models/face_detection_yunet_2026may.onnx</c>. Not set or
    /// missing: faces can't be detected, so every upload goes to review.
    /// </summary>
    public string? FaceModelPath { get; set; }

    /// <summary>
    /// The LPD-YuNet number plate detection model (ONNX), e.g.
    /// <c>/opt/bjk/models/license_plate_detection_lpd_yunet_2023mar.onnx</c>. Not set or missing: every upload goes to review.
    /// </summary>
    public string? PlateModelPath { get; set; }

    /// <summary>Faces scoring at least this are blurred.</summary>
    [Range(0.05, 1.0)]
    public double FaceMinConfidence { get; set; } = 0.5;

    /// <summary>A face (or, in a video, a face's whole track) that never scores this much is uncertain: the media goes to review.</summary>
    [Range(0.05, 1.0)]
    public double FaceConfidentAt { get; set; } = 0.8;

    /// <summary>Number plates scoring at least this are blurred.</summary>
    [Range(0.05, 1.0)]
    public double PlateMinConfidence { get; set; } = 0.5;

    /// <summary>A plate (or a plate's whole track) that never scores this much is uncertain: the media goes to review.</summary>
    [Range(0.05, 1.0)]
    public double PlateConfidentAt { get; set; } = 0.8;

    /// <summary>Extra margin added to every side of a detected box, as a fraction of its width or height.</summary>
    [Range(0.0, 1.0)]
    public double BoxPadding { get; set; } = 0.25;

    /// <summary>
    /// Videos: run the detectors on every Nth frame and follow each face or plate in between, so blurs neither
    /// flicker nor drop out. Lower is safer and slower.
    /// </summary>
    [Range(1, 30)]
    public int DetectEveryNthFrame { get; set; } = 5;

    /// <summary>
    /// Videos: a face or plate missed by this many detection passes in a row still counts as the same one if it
    /// reappears, and stays blurred in between.
    /// </summary>
    [Range(0, 30)]
    public int MaxMissedDetections { get; set; } = 3;

    /// <summary>
    /// Also look for plates in zoomed-in tiles of each frame, not just the whole frame. Finds small, distant plates
    /// that the whole-frame pass misses, at about six times the plate detection cost.
    /// </summary>
    public bool PlateTiles { get; set; } = true;

    /// <summary>Remove the soundtrack from every video (voices, music, in-car conversation). On by default at launch.</summary>
    public bool RemoveAudio { get; set; } = true;

    /// <summary>Send every upload to review even when detection was confident. For launch or incident periods.</summary>
    public bool ReviewEverything { get; set; }

    /// <summary>
    /// Gives up blurring one video after this long (then retried, and Failed after the last attempt). Keep it under
    /// RabbitMQ's consumer acknowledgement timeout (30 minutes by default).
    /// </summary>
    public TimeSpan VideoTimeout { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>Threads ONNX Runtime may use per detection; 0 lets it decide (all cores).</summary>
    [Range(0, 64)]
    public int InferenceThreads { get; set; }
}
