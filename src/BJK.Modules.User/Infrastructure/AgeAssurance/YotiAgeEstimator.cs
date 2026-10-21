using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>
/// Yoti's facial age estimation, used when <c>AgeAssurance:Yoti:SdkId</c> and <c>PrivateKey</c> are set. One frame
/// from the start of the selfie video (where the person faces the camera, before turning their head) is sent to Yoti's
/// <c>/age-antispoofing</c> endpoint, which also checks the picture isn't a spoof. Yoti answers with an age and a
/// margin of error; a spoof, a margin over <see cref="YotiOptions.MaxUncertainty"/> or a picture Yoti can't use
/// counts as "couldn't tell", which offers photo ID. Other errors throw, so the check fails rather than passing anyone.
/// Neither the video, the frame nor Yoti's answer is logged or kept.
/// </summary>
internal sealed class YotiAgeEstimator(
    IHttpClientFactory clients,
    IOptionsMonitor<AgeAssuranceOptions> options,
    TimeProvider clock,
    ILogger<YotiAgeEstimator> logger)
    : IAgeEstimator
{
    public const string ClientName = "BJK.Yoti";
    public const string ProviderName = "Yoti";
    internal const string Endpoint = "/age-antispoofing";

    public string Name => ProviderName;

    public bool IsDevelopmentOnly => false;

    public bool IsConfigured => options.CurrentValue.Yoti.IsConfigured;

    public async Task<AgeEstimate> EstimateAsync(SelfieVideo video, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Yoti;
        if (!YotiRequestSigner.TryLoad(settings.PrivateKey, out var key))
        {
            throw new InvalidOperationException("AgeAssurance:Yoti:PrivateKey isn't a PEM private key.");
        }

        using (key)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.Timeout);

            var frame = await SelfieFrame.ExtractAsync(video, settings.FfmpegPath, timeout.Token).ConfigureAwait(false);
            byte[] body;
            try
            {
                body = JsonSerializer.SerializeToUtf8Bytes(new { img = Convert.ToBase64String(frame) });
            }
            finally
            {
                Array.Clear(frame);
            }

            var query = YotiRequestSigner.Query(Guid.NewGuid(), clock.GetUtcNow());
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl.AbsoluteUri.TrimEnd('/') + Endpoint + query));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Yoti-Auth-Id", settings.SdkId);
            request.Headers.TryAddWithoutValidation("X-Yoti-Auth-Digest", YotiRequestSigner.Sign(key, "POST", Endpoint + query, body));

            var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            Array.Clear(body);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(stream, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.BadRequest && Code(json) is { } code && UnusablePicture.Contains(code))
            {
                // The picture itself (no face, two faces, too dark...): the person can try again or use photo ID.
                logger.LogInformation("Yoti couldn't use the selfie frame ({Code}).", code);
                return new AgeEstimate(null, 0, ProviderName);
            }

            if (!response.IsSuccessStatusCode || json is null)
            {
                // The error code only: the body could echo personal data.
                logger.LogError("Yoti answered HTTP {Status} ({Code}).", (int)response.StatusCode, Code(json) ?? "no code");
                throw new HttpRequestException($"Yoti answered HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            }

            return Read(json.RootElement, settings.MaxUncertainty);
        }
    }

    /// <summary>Yoti's 400 codes for a picture it can't use: these count as "couldn't tell", not as an error.</summary>
    internal static readonly HashSet<string> UnusablePicture = new(StringComparer.Ordinal)
    {
        "FACE_NOT_FOUND", "MULTIPLE_FACES", "FACE_BOX_TOO_SMALL", "FACE_TO_IMAGE_RATIO_TOO_LOW",
        "FACE_TO_IMAGE_RATIO_TOO_HIGH", "INSUFFICIENT_AREA_AROUND_THE_FACE", "IMAGE_TOO_BRIGHT", "IMAGE_TOO_DARK",
        "IMAGE_GRAYSCALE_NOT_SUPPORTED",
    };

    /// <summary>
    /// Reads Yoti's answer: <c>age.age</c> and <c>age.st_dev</c>, and the anti-spoofing <c>prediction</c> (real, fake
    /// or undetermined). Anything but a real person with a margin within <paramref name="maxUncertainty"/> is "couldn't tell".
    /// </summary>
    internal static AgeEstimate Read(JsonElement root, double maxUncertainty)
    {
        var prediction = Text(root, "antispoofing", "prediction") ?? Text(root, "prediction");
        if (prediction is not null && !string.Equals(prediction, "real", StringComparison.OrdinalIgnoreCase))
        {
            return new AgeEstimate(null, 0, ProviderName);
        }

        var age = Number(root, "age", "age") ?? Number(root, "age");
        var uncertainty = Number(root, "age", "st_dev") ?? Number(root, "st_dev") ?? 0;
        return age is null || uncertainty > maxUncertainty
            ? new AgeEstimate(null, 0, ProviderName)
            : new AgeEstimate(age, uncertainty, ProviderName);
    }

    private static async Task<JsonDocument?> ReadJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Code(JsonDocument? json) =>
        json is null ? null : Text(json.RootElement, "error_code") ?? Text(json.RootElement, "code");

    private static JsonElement? At(JsonElement root, string[] path)
    {
        var current = root;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? Text(JsonElement root, params string[] path) =>
        At(root, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? Number(JsonElement root, params string[] path) =>
        At(root, path) switch
        {
            { ValueKind: JsonValueKind.Number } value => value.GetDouble(),
            { ValueKind: JsonValueKind.String } value when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
}

/// <summary>
/// Yoti's request signing: the method, the path with its <c>nonce</c> and <c>timestamp</c> query, and the base64 body,
/// joined with <c>&amp;</c>, signed with SHA256withRSA (PKCS#1 v1.5) using the application's private key, base64.
/// </summary>
internal static class YotiRequestSigner
{
    /// <summary>Loads the PEM private key; false if it isn't one.</summary>
    public static bool TryLoad(string? pem, out RSA key)
    {
        key = RSA.Create();
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            key.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The query every request carries: a fresh UUID nonce and the UNIX time in seconds.</summary>
    public static string Query(Guid nonce, DateTimeOffset now) =>
        $"?nonce={nonce:D}&timestamp={now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The <c>X-Yoti-Auth-Digest</c> header value.</summary>
    public static string Sign(RSA key, string method, string pathAndQuery, byte[]? body)
    {
        var message = body is { Length: > 0 }
            ? $"{method}&{pathAndQuery}&{Convert.ToBase64String(body)}"
            : $"{method}&{pathAndQuery}";
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

/// <summary>
/// Takes one picture from the selfie video with ffmpeg, from just after the start, when the person faces the camera.
/// JPEG at high quality, no wider than 1280 pixels; PNG when the JPEG would be under Yoti's 50 KB minimum. The video
/// goes to a temporary file only for as long as ffmpeg needs it.
/// </summary>
internal static class SelfieFrame
{
    private const int MinBytes = 50 * 1024;

    public static async Task<byte[]> ExtractAsync(SelfieVideo video, string ffmpeg, CancellationToken cancellationToken)
    {
        var input = Path.Combine(Path.GetTempPath(), $"bjk-selfie-{Guid.NewGuid():N}");
        try
        {
            await using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await file.WriteAsync(video.Content, cancellationToken).ConfigureAwait(false);
            }

            var jpeg = await RunAsync(ffmpeg, input, "mjpeg", cancellationToken).ConfigureAwait(false);
            if (jpeg.Length >= MinBytes)
            {
                return jpeg;
            }

            Array.Clear(jpeg);
            return await RunAsync(ffmpeg, input, "png", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(input);
        }
    }

    private static async Task<byte[]> RunAsync(string ffmpeg, string input, string codec, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-ss", "0.3", "-i", input, "-frames:v", "1",
                     "-vf", "scale='min(1280,iw)':-2", "-q:v", "2", "-c:v", codec, "-f", "image2pipe", "pipe:1",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg couldn't be started.");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(copy, errors, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException("ffmpeg couldn't read a picture from the selfie video.");
        }

        return output.ToArray();
    }
}
