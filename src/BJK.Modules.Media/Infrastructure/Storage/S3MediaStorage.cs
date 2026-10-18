using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BJK.Modules.Media.Domain;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Storage;

/// <summary><see cref="IMediaStorage"/> over the S3 API, configured for Cloudflare R2.</summary>
internal sealed class S3MediaStorage(IAmazonS3 s3, IOptions<MediaStorageOptions> options, TimeProvider clock)
    : IMediaStorage
{
    private readonly MediaStorageOptions _options = options.Value;

    // Signed links use the store's own scheme: HTTPS for R2, plain HTTP for a local stand-in on http://localhost.
    private readonly Protocol _protocol =
        options.Value.ServiceUrl?.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;

    public static IAmazonS3 CreateClient(MediaStorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl!.ToString(),
            ForcePathStyle = true,
            // R2 accepts SigV4 with the region "auto".
            AuthenticationRegion = "auto",
            // R2 doesn't support the SDK's newer default checksums; only send them when an operation requires one.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), config);
    }

    public async Task<SignedUpload> CreateOriginalUploadAsync(
        string key,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var expiresAt = clock.GetUtcNow() + lifetime;
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.OriginalsBucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = _protocol,
        });

        return new SignedUpload(
            new Uri(url),
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            expiresAt);
    }

    public async Task<StoredFile?> FindOriginalAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.OriginalsBucket, Key = key },
                cancellationToken);

            return new StoredFile((long?)metadata.ContentLength ?? 0, metadata.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteOriginalAsync(string key, CancellationToken cancellationToken) =>
        s3.DeleteObjectAsync(_options.OriginalsBucket, key, cancellationToken);

    public async Task DownloadOriginalAsync(string key, Stream destination, CancellationToken cancellationToken)
    {
        using var response = await s3.GetObjectAsync(_options.OriginalsBucket, key, cancellationToken);
        await response.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadPublicAsync(string key, string filePath, string contentType, CancellationToken cancellationToken)
    {
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _options.PublicBucket,
                Key = key,
                FilePath = filePath,
                ContentType = contentType,
                // Keys never change once written (a new upload gets a new media id), so caching for a year is safe.
                Headers = { CacheControl = "public, max-age=31536000, immutable" },
            },
            cancellationToken);
    }

    public async Task DeletePublicAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            await s3.DeleteObjectAsync(_options.PublicBucket, key, cancellationToken);
        }
    }

    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var prefix = MediaKeys.UserFolder(userId);
        foreach (var bucket in new[] { _options.OriginalsBucket!, _options.PublicBucket! })
        {
            string? continuation = null;
            do
            {
                var page = await s3.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = continuation },
                    cancellationToken);

                // One at a time: R2 and the SDK's multi-delete checksums don't always agree, and users have few files.
                foreach (var key in page.S3Objects?.Select(o => o.Key) ?? Enumerable.Empty<string>())
                {
                    await s3.DeleteObjectAsync(bucket, key, cancellationToken);
                }

                continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (continuation is not null);
        }
    }

    public async Task<Uri> CreateOriginalDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.OriginalsBucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = (clock.GetUtcNow() + lifetime).UtcDateTime,
            Protocol = _protocol,
        });

        return new Uri(url);
    }

    // Tolerates the base address being configured with or without a trailing slash.
    public Uri PublicUrl(string key) => new(new Uri(_options.PublicBaseUrl!.ToString().TrimEnd('/') + "/"), key);
}
