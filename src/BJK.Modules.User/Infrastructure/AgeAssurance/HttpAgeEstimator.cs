using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure.AgeAssurance;

/// <summary>
/// Generic adapter for a specialist facial age estimation API (<c>AgeAssurance:Estimator:Provider=Http</c>).
/// POSTs the selfie video as multipart/form-data with the API key in a header, then reads the estimated age (and an
/// optional margin of error) from the JSON response at the configured paths. Nothing about the video or the
/// response body is logged. A vendor with a different request shape gets its own <see cref="IAgeEstimator"/>.
/// </summary>
internal sealed class HttpAgeEstimator(
    IHttpClientFactory clients,
    IOptionsMonitor<AgeAssuranceOptions> options,
    ILogger<HttpAgeEstimator> logger)
    : IAgeEstimator
{
    public const string ClientName = "BJK.AgeEstimator";

    public string Name => AgeAssuranceOptions.HttpProvider;

    public bool IsDevelopmentOnly => false;

    public async Task<AgeEstimate> EstimateAsync(SelfieVideo video, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Estimator.Http;
        var endpoint = settings.Endpoint
            ?? throw new InvalidOperationException("AgeAssurance:Estimator:Http:Endpoint is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        using var content = new MultipartFormDataContent();
        var file = new ReadOnlyMemoryContent(video.Content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(video.ContentType);
        content.Add(file, settings.VideoField, "selfie" + ExtensionFor(video.ContentType));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.TryAddWithoutValidation(settings.ApiKeyHeader, settings.ApiKeyPrefix + settings.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = clients.CreateClient(ClientName);
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the body could echo personal data.
            logger.LogError("Age estimation provider {Provider} answered HTTP {Status}.", settings.Name, (int)response.StatusCode);
            throw new HttpRequestException(
                $"The age estimation provider answered HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        return Read(json.RootElement, settings);
    }

    /// <summary>Reads the estimate from the provider's JSON. A missing or non-numeric age means "couldn't tell".</summary>
    internal static AgeEstimate Read(JsonElement root, HttpAgeEstimatorOptions settings)
    {
        var age = ReadNumber(root, settings.AgePath);
        var uncertainty = string.IsNullOrWhiteSpace(settings.UncertaintyPath) ? null : ReadNumber(root, settings.UncertaintyPath);
        return new AgeEstimate(age, uncertainty ?? 0, settings.Name);
    }

    internal static double? ReadNumber(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.String when double.TryParse(current.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string ExtensionFor(string contentType) =>
        contentType.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4"
        : contentType.StartsWith("video/quicktime", StringComparison.OrdinalIgnoreCase) ? ".mov"
        : ".webm";
}
