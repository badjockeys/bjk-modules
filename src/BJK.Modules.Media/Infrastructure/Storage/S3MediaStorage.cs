using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Storage;

/// <summary><see cref="IMediaStorage"/> over the S3 API, configured for Cloudflare R2.</summary>
internal sealed class S3MediaStorage(IAmazonS3 s3, IOptions<MediaStorageOptions> options, TimeProvider clock)
    : IMediaStorage
{
    private readonly MediaStorageOptions _options = options.Value;

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
            Protocol = Protocol.HTTPS,
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

    public async Task DeletePublicAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            await s3.DeleteObjectAsync(_options.PublicBucket, key, cancellationToken);
        }
    }

    // Tolerates the base address being configured with or without a trailing slash.
    public Uri PublicUrl(string key) => new(new Uri(_options.PublicBaseUrl!.ToString().TrimEnd('/') + "/"), key);
}
