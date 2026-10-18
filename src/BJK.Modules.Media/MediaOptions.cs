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

/// <summary>RabbitMQ. Section <c>Media:Queue</c>.</summary>
public sealed class MediaQueueOptions
{
    public const string SectionName = "Media:Queue";

    /// <summary>e.g. amqp://user:password@host:5672/vhost. Secret.</summary>
    [Required]
    public Uri? ConnectionUri { get; set; }

    [Required]
    public string QueueName { get; set; } = "media.process";
}
