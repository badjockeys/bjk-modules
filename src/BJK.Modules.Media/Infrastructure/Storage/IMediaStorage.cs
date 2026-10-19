namespace BJK.Modules.Media.Infrastructure.Storage;

/// <summary>
/// Object storage for media files. Implemented with the S3 API, so it works with Cloudflare R2
/// or any S3-compatible store; no cloud-specific code lives outside this folder.
/// </summary>
public interface IMediaStorage
{
    /// <summary>A short-lived link the app uses to PUT one file straight into the originals bucket.</summary>
    Task<SignedUpload> CreateOriginalUploadAsync(
        string key,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    /// <summary>Details of an uploaded original, or null if nothing has been uploaded at that key.</summary>
    Task<StoredFile?> FindOriginalAsync(string key, CancellationToken cancellationToken);

    Task DeleteOriginalAsync(string key, CancellationToken cancellationToken);

    /// <summary>Copies an original into <paramref name="destination"/>. Used by the processing worker.</summary>
    Task DownloadOriginalAsync(string key, Stream destination, CancellationToken cancellationToken);

    /// <summary>Publishes a processed file to the public bucket, cached by browsers and Cloudflare for a year.</summary>
    Task UploadPublicAsync(string key, string filePath, string contentType, CancellationToken cancellationToken);

    Task DeletePublicAsync(IEnumerable<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a processed file in the private originals bucket, e.g. blurred files waiting for a moderator. Read them
    /// with <see cref="CreateOriginalDownloadUrlAsync"/> and remove them with <see cref="DeleteOriginalAsync"/>.
    /// </summary>
    Task UploadPrivateAsync(string key, string filePath, string contentType, CancellationToken cancellationToken);

    /// <summary>Copies a file from the private originals bucket to the public bucket, e.g. once a moderator approves it.</summary>
    Task PublishPrivateAsync(string privateKey, string publicKey, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every file under the user's folder (<c>{userId}/</c>) in both buckets, including any left behind
    /// by failed uploads. Used when an account is permanently deleted.
    /// </summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>A short-lived link to download an original, for data exports.</summary>
    Task<Uri> CreateOriginalDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>The address a processed file is served from through Cloudflare.</summary>
    Uri PublicUrl(string key);
}

/// <param name="Url">Where to send the file.</param>
/// <param name="Method">Always PUT.</param>
/// <param name="Headers">Headers the app must send exactly as given, or the upload is refused.</param>
/// <param name="ExpiresAt">After this the link stops working.</param>
public sealed record SignedUpload(
    Uri Url,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

public sealed record StoredFile(long SizeBytes, string? ContentType);
