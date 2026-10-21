using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.Infrastructure.Describe;

/// <summary>What the person had filled in when they asked for a description; given to the model as text.</summary>
/// <param name="Types">Incident types, e.g. "Tailgating".</param>
/// <param name="PlaceName">Road and town.</param>
/// <param name="IncidentAt">When it happened.</param>
/// <param name="IsDark">Dark or daylight, if known.</param>
/// <param name="VehicleType">E.g. "Car", "Van".</param>
/// <param name="VehicleMake">E.g. "Ford".</param>
/// <param name="VehicleModel">E.g. "Transit".</param>
/// <param name="VehicleColour">E.g. "White".</param>
public sealed record DescriptionContext(
    IReadOnlyList<string> Types,
    string? PlaceName,
    DateTimeOffset? IncidentAt,
    bool? IsDark,
    string? VehicleType,
    string? VehicleMake,
    string? VehicleModel,
    string? VehicleColour)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DescriptionContext FromJson(string json) =>
        JsonSerializer.Deserialize<DescriptionContext>(json, JsonOptions) ?? new([], null, null, null, null, null, null, null);
}

/// <summary>A draft from the model.</summary>
public sealed record DraftDescription(string Title, string Description, string Model);

/// <summary>Writes a draft title and description from pictures (JPEG) and the context.</summary>
public interface IDescriptionModel
{
    Task<DraftDescription> DescribeAsync(IReadOnlyList<byte[]> pictures, DescriptionContext context, CancellationToken cancellationToken);
}

/// <summary>The instructions and the reading of the model's answer, shared by every model.</summary>
internal static partial class DescriptionPrompt
{
    public const string Instructions =
        "You write short, neutral descriptions of dashcam footage for Bad Jockeys, a UK road safety site where people "
        + "share clips of bad driving. Look at the pictures (frames from one clip, or several photos of one incident; "
        + "faces and number plates are blurred) and the details given. Describe only the driving you can see: what the "
        + "vehicles did, where, and the road and conditions. Rules: never identify anyone; never mention or guess a "
        + "number plate, name, face or anything personal; never say who is at fault or what should happen to them; no "
        + "opinions, jokes or swearing; don't invent anything you can't see or weren't told. Use UK English. Reply with "
        + "JSON only, in this form: {\"title\": \"...\", \"description\": \"...\"}. The title is at most 80 characters. "
        + "The description is two or three plain sentences.";

    public static string Details(DescriptionContext context)
    {
        var lines = new List<string>();
        if (context.Types.Count > 0)
        {
            lines.Add($"Incident type: {string.Join(", ", context.Types.Select(Words))}");
        }

        if (!string.IsNullOrWhiteSpace(context.PlaceName))
        {
            lines.Add($"Where: {context.PlaceName}");
        }

        if (context.IncidentAt is { } at)
        {
            lines.Add($"When: {at:dddd d MMMM yyyy, HH:mm}");
        }

        if (context.IsDark is { } dark)
        {
            lines.Add(dark ? "Light: dark" : "Light: daylight");
        }

        var vehicle = string.Join(' ', new[] { context.VehicleColour, context.VehicleMake, context.VehicleModel, context.VehicleType }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (vehicle.Length > 0)
        {
            lines.Add($"The other vehicle: {vehicle}");
        }

        return lines.Count == 0 ? "No details were given." : string.Join('\n', lines);
    }

    /// <summary>"RedLightRunning" as "red light running".</summary>
    public static string Words(string type) => CapitalRun().Replace(type, " $1").Trim().ToLowerInvariant();

    /// <summary>
    /// Reads <c>{"title", "description"}</c> from the model's answer (it may wrap it in a code block or add text).
    /// Anything that looks like a UK number plate is removed. Null if there's no usable draft.
    /// </summary>
    public static (string Title, string Description)? Read(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var start = answer.IndexOf('{', StringComparison.Ordinal);
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(answer[start..(end + 1)]);
            var title = Clean(Text(json.RootElement, "title"), 80);
            var description = Clean(Text(json.RootElement, "description"), 600);
            return title.Length == 0 || description.Length == 0 ? null : (title, description);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Clean(string? text, int max)
    {
        var cleaned = Plate().Replace(text ?? string.Empty, "a vehicle");
        cleaned = Spaces().Replace(cleaned, " ").Trim();
        if (cleaned.Length <= max)
        {
            return cleaned;
        }

        var cut = cleaned[..max];
        var lastStop = cut.LastIndexOf('.');
        return lastStop > max / 2 ? cut[..(lastStop + 1)] : cut.TrimEnd();
    }

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CapitalRun();

    // Current-style (AB12 CDE), prefix (A123 BCD) and suffix (ABC 123D) plates, with or without the space.
    [GeneratedRegex(@"\b([A-Z]{2}[0-9]{2}\s?[A-Z]{3}|[A-Z][0-9]{1,3}\s?[A-Z]{3}|[A-Z]{3}\s?[0-9]{1,3}[A-Z])\b")]
    private static partial Regex Plate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// A vision language model behind an OpenAI-compatible chat API (<c>POST {Endpoint}/chat/completions</c>): vLLM serving
/// Qwen3-VL on our GPU when deployed, or Ollama locally. The pictures go as base64 data URLs; nothing is logged.
/// </summary>
internal sealed class OpenAiCompatibleDescriptionModel(
    HttpClient http,
    IOptions<DescribeOptions> options,
    ILogger<OpenAiCompatibleDescriptionModel> logger)
    : IDescriptionModel
{
    public async Task<DraftDescription> DescribeAsync(IReadOnlyList<byte[]> pictures, DescriptionContext context, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var endpoint = settings.Endpoint ?? throw new InvalidOperationException("Media:Describe:Endpoint isn't set.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        var content = new List<object> { new { type = "text", text = DescriptionPrompt.Details(context) } };
        content.AddRange(pictures.Select(p => new
        {
            type = "image_url",
            image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(p) },
        }));
        var body = new
        {
            model = settings.Model,
            temperature = 0.2,
            max_tokens = 400,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = DescriptionPrompt.Instructions },
                new { role = "user", content },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions"))
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("The description model answered HTTP {Status}.", (int)response.StatusCode);
            throw new HttpRequestException($"The description model answered HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletion>(timeout.Token).ConfigureAwait(false);
        var answer = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        var draft = DescriptionPrompt.Read(answer)
            ?? throw new InvalidOperationException("The description model's answer had no usable title and description.");
        return new DraftDescription(draft.Title, draft.Description, completion?.Model ?? settings.Model);
    }

    private sealed record ChatCompletion(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);

    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}

/// <summary>
/// DEVELOPMENT ONLY: used when <c>Media:Describe:Endpoint</c> isn't set. Writes a fixed draft from the details given,
/// without looking at the pictures, so the button can be tried on any laptop. Refuses outside Development.
/// </summary>
internal sealed class DevelopmentDescriptionModel(IHostEnvironment environment, ILogger<DevelopmentDescriptionModel> logger) : IDescriptionModel
{
    public const string Name = "Development";

    public Task<DraftDescription> DescribeAsync(IReadOnlyList<byte[]> pictures, DescriptionContext context, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Media:Describe:Endpoint isn't set: the stand-in only runs in Development.");
        }

        logger.LogWarning("DEVELOPMENT ONLY - description written by the stand-in, not a model ({Pictures} pictures).", pictures.Count);
        var type = context.Types.Count > 0 ? DescriptionPrompt.Words(context.Types[0]) : "bad driving";
        var place = string.IsNullOrWhiteSpace(context.PlaceName) ? null : context.PlaceName;
        var vehicle = string.Join(' ', new[] { context.VehicleColour, context.VehicleMake, context.VehicleModel }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        var title = Capitalise(place is null ? type : $"{type} on {place.Split(',')[0]}");
        var description = new StringBuilder()
            .Append(vehicle.Length > 0 ? $"A {vehicle.ToLowerInvariant()} " : "A vehicle ")
            .Append(place is null ? "was caught on camera" : $"was caught on camera on {place}")
            .Append(context.IsDark == true ? " after dark. " : ". ")
            .Append($"The clip shows {type}. (Example draft: no model is set up on this computer.)")
            .ToString();
        return Task.FromResult(new DraftDescription(title, description, Name));
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
