using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BJK.Modules.Shared.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Notifications.Infrastructure;

/// <summary>A UK place for a home area: a town or village, or a postcode area. Kept to about 1 km.</summary>
/// <param name="Name">What to call it, e.g. "Rainhill" or "WA10".</param>
/// <param name="Detail">Where it is, e.g. "St. Helens, North West".</param>
public sealed record PlaceDto(string Name, string? Detail, decimal Latitude, decimal Longitude);

/// <summary>Finds UK places by name or postcode.</summary>
public interface IPlaceLookup
{
    Task<IReadOnlyList<PlaceDto>> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// postcodes.io (free, no key, Open Government Licence data): place names through <c>/places</c>, full postcodes
/// through <c>/postcodes</c> and outward codes (e.g. "WA10") through <c>/outcodes</c>. Only the outward code is kept for a
/// full postcode, so a home address is never stored. Results are cached for a day.
/// </summary>
internal sealed partial class PostcodesIoPlaceLookup(HttpClient http, IMemoryCache cache, ILogger<PostcodesIoPlaceLookup> logger)
    : IPlaceLookup
{
    public const string HttpClientName = "postcodes.io";

    public async Task<IReadOnlyList<PlaceDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        if (q.Length < 2)
        {
            return [];
        }

        var key = "places:" + q.ToUpperInvariant();
        if (cache.TryGetValue(key, out IReadOnlyList<PlaceDto>? cached) && cached is not null)
        {
            return cached;
        }

        IReadOnlyList<PlaceDto> found;
        try
        {
            found = FullPostcode().IsMatch(q) ? await PostcodeAsync(q, cancellationToken)
                : Outcode().IsMatch(q) ? await OutcodeAsync(q, cancellationToken)
                : await PlacesAsync(q, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The place lookup failed.");
            throw new ServiceUnavailableException("Place search isn't working right now. Try again in a minute.");
        }

        cache.Set(key, found, TimeSpan.FromDays(1));
        return found;
    }

    private async Task<IReadOnlyList<PlaceDto>> PostcodeAsync(string postcode, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"postcodes/{Uri.EscapeDataString(postcode)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await response.Content.ReadFromJsonAsync<Envelope<PostcodeResult>>(cancellationToken);
        var r = body?.Result;
        return r?.Latitude is null || r.Longitude is null
            ? []
            : [Place(r.Outcode ?? postcode.Split(' ')[0].ToUpperInvariant(), Join(r.AdminDistrict, r.Region), r.Latitude.Value, r.Longitude.Value)];
    }

    private async Task<IReadOnlyList<PlaceDto>> OutcodeAsync(string outcode, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"outcodes/{Uri.EscapeDataString(outcode)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await response.Content.ReadFromJsonAsync<Envelope<OutcodeResult>>(cancellationToken);
        var r = body?.Result;
        return r?.Latitude is null || r.Longitude is null
            ? []
            : [Place(r.Outcode ?? outcode.ToUpperInvariant(), r.AdminDistrict?.FirstOrDefault(), r.Latitude.Value, r.Longitude.Value)];
    }

    private async Task<IReadOnlyList<PlaceDto>> PlacesAsync(string name, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync($"places?q={Uri.EscapeDataString(name)}&limit=10", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var body = await response.Content.ReadFromJsonAsync<Envelope<List<PlaceResult>>>(cancellationToken);
        return (body?.Result ?? [])
            .Where(p => p.Name is not null && p.Latitude is not null && p.Longitude is not null)
            .Select(p => Place(p.Name!, Join(p.DistrictBorough ?? p.CountyUnitary, p.Region), p.Latitude!.Value, p.Longitude!.Value))
            .DistinctBy(p => (p.Name, p.Detail))
            .ToList();
    }

    private static PlaceDto Place(string name, string? detail, double latitude, double longitude) => new(
        name,
        detail,
        Math.Round((decimal)latitude, 2, MidpointRounding.AwayFromZero),
        Math.Round((decimal)longitude, 2, MidpointRounding.AwayFromZero));

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"^[A-Za-z]{1,2}\d[A-Za-z\d]?\s*\d[A-Za-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullPostcode();

    [GeneratedRegex(@"^[A-Za-z]{1,2}\d[A-Za-z\d]?$", RegexOptions.CultureInvariant)]
    private static partial Regex Outcode();

    private sealed record Envelope<T>([property: JsonPropertyName("result")] T? Result);

    private sealed record PostcodeResult(
        [property: JsonPropertyName("outcode")] string? Outcode,
        [property: JsonPropertyName("admin_district")] string? AdminDistrict,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude);

    private sealed record OutcodeResult(
        [property: JsonPropertyName("outcode")] string? Outcode,
        [property: JsonPropertyName("admin_district")] List<string>? AdminDistrict,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude);

    private sealed record PlaceResult(
        [property: JsonPropertyName("name_1")] string? Name,
        [property: JsonPropertyName("district_borough")] string? DistrictBorough,
        [property: JsonPropertyName("county_unitary")] string? CountyUnitary,
        [property: JsonPropertyName("region")] string? Region,
        [property: JsonPropertyName("latitude")] double? Latitude,
        [property: JsonPropertyName("longitude")] double? Longitude);
}
