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
