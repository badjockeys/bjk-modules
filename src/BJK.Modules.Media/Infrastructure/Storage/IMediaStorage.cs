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

    /// <summary>
    /// A short-lived link to download a file from the private originals bucket, saved under <paramref name="fileName"/>
    /// (the link asks the browser to download it rather than play it).
    /// </summary>
    /// <param name="key">The file's key.</param>
    /// <param name="lifetime">How long the link works.</param>
    /// <param name="fileName">The name it's saved as: ours, never anything the uploader typed.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<Uri> CreateOriginalDownloadUrlAsync(string key, TimeSpan lifetime, string fileName, CancellationToken cancellationToken) =>
        CreateOriginalDownloadUrlAsync(key, lifetime, cancellationToken);

    /// <summary>The address a processed file is served from through Cloudflare.</summary>
    Uri PublicUrl(string key);

    /// <summary>
    /// Stores a file in a bucket. Public files are cached by browsers and Cloudflare for a year; files in the other
    /// buckets are private.
    /// </summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="filePath">The local file to upload.</param>
    /// <param name="contentType">Its type.</param>
    /// <param name="cancellationToken">Stops the upload.</param>
    Task UploadAsync(MediaBucket bucket, string key, string filePath, string contentType, CancellationToken cancellationToken);

    /// <summary>Copies a file from a bucket into <paramref name="destination"/>.</summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="destination">Where to write it.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    Task DownloadAsync(MediaBucket bucket, string key, Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a file from one bucket to another (or within one), replacing anything already at the destination, with
    /// the destination bucket's headers. Safe to repeat.
    /// </summary>
    /// <param name="from">The source bucket.</param>
    /// <param name="fromKey">The source key.</param>
    /// <param name="to">The destination bucket.</param>
    /// <param name="toKey">The destination key.</param>
    /// <param name="contentType">The file's type.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    Task CopyAsync(MediaBucket from, string fromKey, MediaBucket to, string toKey, string contentType, CancellationToken cancellationToken);

    /// <summary>True if a file exists at the key.</summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<bool> ExistsAsync(MediaBucket bucket, string key, CancellationToken cancellationToken);

    /// <summary>Deletes files; keys with nothing there are ignored.</summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="keys">The files' keys.</param>
    /// <param name="cancellationToken">Stops deleting.</param>
    Task DeleteAsync(MediaBucket bucket, IEnumerable<string> keys, CancellationToken cancellationToken);

    /// <summary>A short-lived signed link to read a file in a private bucket (e.g. sensitive media for an age-verified adult).</summary>
    /// <param name="bucket">Which bucket.</param>
    /// <param name="key">The file's key.</param>
    /// <param name="lifetime">How long the link works.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    Task<Uri> CreateDownloadUrlAsync(MediaBucket bucket, string key, TimeSpan lifetime, CancellationToken cancellationToken);
}

/// <summary>The buckets media files live in.</summary>
public enum MediaBucket
{
    /// <summary>Private: uploaded originals (deleted after 90 days), plus files waiting for review and video segments.</summary>
    Originals,

    /// <summary>Public, served through Cloudflare: processed files, and only the blurred preview of sensitive media.</summary>
    Public,

    /// <summary>
    /// Private: the real processed files of sensitive media, read only through short-lived signed links.
    /// <c>Media:Storage:SensitiveBucket</c>, or the originals bucket if that isn't set.
    /// </summary>
    Sensitive,
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
