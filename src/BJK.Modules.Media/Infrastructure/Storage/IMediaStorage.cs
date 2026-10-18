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

    Task DeletePublicAsync(IEnumerable<string> keys, CancellationToken cancellationToken);

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
