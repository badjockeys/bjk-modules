using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Notifications.Infrastructure.Push;

/// <summary>
/// Message encryption for web push (RFC 8291, "aes128gcm" content coding from RFC 8188): one record, encrypted to the
/// browser's public key and authentication secret with a fresh key pair and salt each time.
/// </summary>
internal static class WebPushEncryption
{
    /// <summary>The record size written in the header.</summary>
    public const int RecordSize = 4096;

    /// <summary>
    /// The largest plaintext that fits: push services take bodies up to 4096 bytes, less the 86-byte header, the
    /// 16-byte tag and the padding delimiter.
    /// </summary>
    public const int MaxPlaintext = 4096 - 86 - 16 - 1;

    private static readonly byte[] KeyInfoLabel = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    /// <summary>Encrypts <paramref name="plaintext"/> for one browser. Returns the request body (header and ciphertext).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] browserPublicKey, byte[] authSecret)
    {
        using var senderKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, browserPublicKey, authSecret, senderKey, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>As above with a given sender key and salt (the RFC's example, in tests).</summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        byte[] browserPublicKey,
        byte[] authSecret,
        ECDiffieHellman senderKey,
        byte[] salt)
    {
        if (plaintext.Length > MaxPlaintext)
        {
            throw new ArgumentException($"A push message can be at most {MaxPlaintext} bytes.", nameof(plaintext));
        }

        if (browserPublicKey is not { Length: 65 } || browserPublicKey[0] != 0x04)
        {
            throw new ArgumentException("The browser's key must be an uncompressed P-256 point.", nameof(browserPublicKey));
        }

        if (authSecret is not { Length: 16 })
        {
            throw new ArgumentException("The authentication secret must be 16 bytes.", nameof(authSecret));
        }

        using var browserKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = browserPublicKey[1..33], Y = browserPublicKey[33..65] },
        });
        var senderPublicKey = PublicKeyBytes(senderKey.ExportParameters(includePrivateParameters: false));
        var ecdhSecret = senderKey.DeriveRawSecretAgreement(browserKey.PublicKey);

        // key_info = "WebPush: info" || 0x00 || ua_public || as_public; IKM = HKDF(auth_secret, ecdh_secret, key_info, 32).
        var keyInfo = new byte[KeyInfoLabel.Length + 65 + 65];
        KeyInfoLabel.CopyTo(keyInfo, 0);
        browserPublicKey.CopyTo(keyInfo, KeyInfoLabel.Length);
        senderPublicKey.CopyTo(keyInfo, KeyInfoLabel.Length + 65);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, CekInfo);
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, NonceInfo);

        // One record: the plaintext, then 0x02 (the last record's delimiter), no further padding.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;

        // Header: salt (16) || record size (4, big-endian) || key id length (1) || key id (the sender's public key, 65).
        var body = new byte[16 + 4 + 1 + 65 + padded.Length + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = 65;
        senderPublicKey.CopyTo(body, 21);
        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, padded, body.AsSpan(86, padded.Length), body.AsSpan(86 + padded.Length, 16));
        return body;
    }

    internal static byte[] PublicKeyBytes(ECParameters parameters)
    {
        var key = new byte[65];
        key[0] = 0x04;
        parameters.Q.X!.CopyTo(key, 1);
        parameters.Q.Y!.CopyTo(key, 33);
        return key;
    }
}

/// <summary>
/// The VAPID signature (RFC 8292): a short-lived ES256 JWT for the push service's origin, signed with this server's
/// private key, sent with the public key.
/// </summary>
internal sealed class VapidSigner : IDisposable
{
    private static readonly string Header = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);

    private readonly ECDsa _key;
    private readonly string _subject;

    public VapidSigner(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var publicKey = Base64Url.DecodeFromChars(options.PublicKey);
        var privateKey = Base64Url.DecodeFromChars(options.PrivateKey);
        if (publicKey is not { Length: 65 } || publicKey[0] != 0x04 || privateKey is not { Length: 32 })
        {
            throw new InvalidOperationException(
                "Notifications:Push keys must be a P-256 key pair: an 87-character public key and a 43-character private key, base64url.");
        }

        _key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        PublicKey = options.PublicKey!;
        _subject = options.Subject;
    }

    /// <summary>The public key as configured (base64url), for the browser and the <c>k</c> parameter.</summary>
    public string PublicKey { get; }

    /// <summary>The Authorization header value for a push service at <paramref name="endpoint"/>, valid for 12 hours.</summary>
    public string Authorization(Uri endpoint, DateTimeOffset now)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = _subject,
        });
        var unsigned = $"{Header}.{Base64Url.EncodeToString(claims)}";

        // .NET signs ECDSA in the fixed r||s form JWTs use.
        var signature = _key.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256);
        return $"vapid t={unsigned}.{Base64Url.EncodeToString(signature)}, k={PublicKey}";
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>What a push service said.</summary>
internal enum PushOutcome
{
    /// <summary>Accepted for delivery.</summary>
    Sent,

    /// <summary>The subscription has gone (404 or 410): forget it.</summary>
    Gone,

    /// <summary>Anything else: logged, and the subscription kept.</summary>
    Failed,
}

/// <summary>Sends one encrypted message to one browser's push service.</summary>
internal interface IWebPushClient
{
    /// <summary>True when the VAPID keys are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>The VAPID public key, or null when push is off.</summary>
    string? PublicKey { get; }

    Task<PushOutcome> SendAsync(PushSubscription subscription, byte[] payload, CancellationToken cancellationToken);
}

internal sealed class WebPushClient : IWebPushClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly PushOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebPushClient> _logger;
    private readonly VapidSigner? _signer;

    public WebPushClient(HttpClient http, IOptions<NotificationsOptions> options, TimeProvider clock, ILogger<WebPushClient> logger)
    {
        _http = http;
        _options = options.Value.Push;
        _clock = clock;
        _logger = logger;
        _signer = CreateSigner(_options, logger);
    }

    public bool IsConfigured => _signer is not null;

    public string? PublicKey => _signer?.PublicKey;

    public async Task<PushOutcome> SendAsync(PushSubscription subscription, byte[] payload, CancellationToken cancellationToken)
    {
        // Checked when subscribing too; checked again so nothing else is ever called.
        if (_signer is null || !PushServices.IsAllowed(subscription.Endpoint))
        {
            return PushOutcome.Failed;
        }

        var endpoint = new Uri(subscription.Endpoint);
        var body = WebPushEncryption.Encrypt(
            payload,
            Base64Url.DecodeFromChars(subscription.P256dh),
            Base64Url.DecodeFromChars(subscription.Auth));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", ((int)_options.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", _signer.Authorization(endpoint, _clock.GetUtcNow()));

        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushOutcome.Sent;
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushOutcome.Gone;
        }

        _logger.LogWarning("Push service {Host} refused a message: {Status}.", endpoint.Host, (int)response.StatusCode);
        return PushOutcome.Failed;
    }

    public void Dispose() => _signer?.Dispose();

    /// <summary>The signer, or null (push off) when the keys aren't set or aren't a valid pair: never throws.</summary>
    private static VapidSigner? CreateSigner(PushOptions options, ILogger logger)
    {
        if (!options.IsConfigured)
        {
            return null;
        }

        try
        {
            return new VapidSigner(options);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or CryptographicException)
        {
            logger.LogError(ex, "Notifications:Push keys aren't a valid VAPID key pair, so push notifications are off.");
            return null;
        }
    }
}

/// <summary>What a push notification shows, and where tapping it goes (a path in the web app).</summary>
internal sealed record PushMessage(string Title, string Body, string Url, string Tag);

/// <summary>Sends a push notification to every device a person turned push on for.</summary>
internal interface IPushSender
{
    /// <summary>False when push isn't configured: nothing is sent.</summary>
    bool IsEnabled { get; }

    Task SendAsync(Guid userId, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Pushes to each of the person's devices at once, and forgets devices the push service says have gone. A device that
/// fails is logged and kept.
/// </summary>
internal sealed class PushSender(INotificationRepository notifications, IWebPushClient client, ILogger<PushSender> logger)
    : IPushSender
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => client.IsConfigured;

    public async Task SendAsync(Guid userId, PushMessage message, CancellationToken cancellationToken)
    {
        if (!client.IsConfigured)
        {
            return;
        }

        var devices = await notifications.ListPushSubscriptionsAsync(userId, cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(Fitted(message), Json);
        var outcomes = await Task.WhenAll(devices.Select(async device =>
        {
            try
            {
                return await client.SendAsync(device, payload, cancellationToken);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or CryptographicException or FormatException or ArgumentException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning(ex, "A push to one of user {UserId}'s devices failed.", userId);
                return PushOutcome.Failed;
            }
        }));

        var gone = devices.Where((_, i) => outcomes[i] == PushOutcome.Gone).ToList();
        if (gone.Count > 0)
        {
            foreach (var device in gone)
            {
                notifications.RemovePushSubscription(device);
            }

            await notifications.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Keeps the message well inside what a push service accepts.</summary>
    private static PushMessage Fitted(PushMessage message) =>
        message.Body.Length <= 1000 ? message : message with { Body = message.Body[..999] + "…" };
}

/// <summary>Checks for the VAPID key settings.</summary>
internal static class PushKeyChecks
{
    public static bool IsPublicKey(string? value) => PushServices.IsPublicKey(value);

    public static bool IsPrivateKey(string? value)
    {
        try
        {
            return value is { Length: 43 } && Base64Url.DecodeFromChars(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
