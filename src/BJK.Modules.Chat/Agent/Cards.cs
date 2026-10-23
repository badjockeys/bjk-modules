using System.Globalization;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Chat.UseCases;

namespace BJK.Modules.Chat.Agent;

/// <summary>Draws answer cards and map links from a query's rows, using the columns the model named.</summary>
internal static class Cards
{
    internal const int MaxPoints = 60;
    internal const int MaxPlaces = 10;
    internal const int MaxTableRows = 20;

    private static readonly string[] LatitudeNames = ["AreaLatitude", "Latitude", "Lat"];
    private static readonly string[] LongitudeNames = ["AreaLongitude", "Longitude", "Lng", "Lon"];
    private static readonly string[] YearNames = ["Year", "IncidentYear"];
    private static readonly string[] MonthNames = ["Month", "IncidentMonth"];
    private static readonly string[] HourNames = ["Hour", "HourUk"];

    public static ChatCardDto? Build(ChatAgent.ModelAnswer answer, QueryResult result)
    {
        if (result.Rows.Count == 0 || result.HiddenRows == result.Rows.Count)
        {
            return null;
        }

        var type = answer.ChartType switch
        {
            "number" => ChatCardType.Number,
            "line" => ChatCardType.Line,
            "bar" => ChatCardType.Bar,
            "places" => ChatCardType.Places,
            "table" => ChatCardType.Table,
            _ => (ChatCardType?)null,
        };
        if (type is not { } cardType)
        {
            return null;
        }

        if (cardType == ChatCardType.Table)
        {
            var rows = result.Rows.Take(MaxTableRows)
                .Select((row, r) => (IReadOnlyList<string?>)row.Select(value => result.Hidden[r] && value is null ? "<3" : Format(value)).ToArray())
                .ToArray();
            return new ChatCardDto(cardType, answer.Title, null, [], result.Columns, rows, result.HiddenRows);
        }

        var value = Find(result, answer.ValueColumn) ?? result.IncidentCountColumn;
        var latitude = Find(result, LatitudeNames);
        var longitude = Find(result, LongitudeNames);
        var year = Find(result, YearNames);
        var month = Find(result, MonthNames);
        var label = Find(result, answer.LabelColumn)
            ?? Enumerable.Range(0, result.Columns.Count).Cast<int?>()
                .FirstOrDefault(i => i != value && i != latitude && i != longitude && i != year);
        if (label == value)
        {
            label = null;
        }

        var limit = cardType switch
        {
            ChatCardType.Number => 1,
            ChatCardType.Places => MaxPlaces,
            _ => MaxPoints,
        };
        var points = new List<ChatCardPointDto>();
        for (var r = 0; r < result.Rows.Count && points.Count < limit; r++)
        {
            var row = result.Rows[r];
            var text = Label(row, label, year, month, result.Columns, cardType);
            points.Add(new ChatCardPointDto(
                text,
                result.Hidden[r] ? null : SmallCounts.AsNumber(row[value]),
                result.Hidden[r],
                cardType == ChatCardType.Places && latitude is { } lat ? SmallCounts.AsNumber(row[lat]) : null,
                cardType == ChatCardType.Places && longitude is { } lng ? SmallCounts.AsNumber(row[lng]) : null));
        }

        return points.All(p => p.Value is null)
            ? null
            : new ChatCardDto(cardType, answer.Title, result.Columns[value], points, null, null, result.HiddenRows);
    }

    /// <summary>"Open on the map": centred on the busiest area in the rows, else the member's place.</summary>
    public static ChatMapLinkDto MapLink(QueryResult result, AgentContext context)
    {
        double? latitude = null, longitude = null;
        if (Find(result, LatitudeNames) is { } lat && Find(result, LongitudeNames) is { } lng)
        {
            var busiest = result.Rows
                .Select((row, r) => (Row: row, Count: result.Hidden[r] ? 0 : SmallCounts.AsNumber(row[result.IncidentCountColumn]) ?? 0))
                .Where(x => SmallCounts.AsNumber(x.Row[lat]) is not null && SmallCounts.AsNumber(x.Row[lng]) is not null)
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();
            if (busiest.Row is not null)
            {
                latitude = SmallCounts.AsNumber(busiest.Row[lat]);
                longitude = SmallCounts.AsNumber(busiest.Row[lng]);
            }
        }

        if (latitude is null && context.Near is { } near)
        {
            (latitude, longitude) = (Math.Round(near.Latitude, 2), Math.Round(near.Longitude, 2));
        }

        var subcategories = Find(result, ["Subcategory"]) is { } column
            ? result.Rows.Select(row => row[column] as string).OfType<string>().Distinct(StringComparer.Ordinal).Take(4).ToArray()
            : Array.Empty<string>();
        return new ChatMapLinkDto(latitude, longitude, latitude is null ? null : 13, subcategories.Length is > 0 and <= 3 ? subcategories : Array.Empty<string>());
    }

    private static string Label(object?[] row, int? label, int? year, int? month, IReadOnlyList<string> columns, ChatCardType type)
    {
        // Year and month columns become "Oct 2026" on trends.
        if (type == ChatCardType.Line && year is { } y && month is { } m
            && SmallCounts.AsNumber(row[y]) is { } yearValue && SmallCounts.AsNumber(row[m]) is { } monthValue
            && monthValue is >= 1 and <= 12)
        {
            return new DateOnly((int)yearValue, (int)monthValue, 1).ToString("MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
        }

        if (label is not { } index)
        {
            return string.Empty;
        }

        // Hours become "08:00".
        if (HourNames.Contains(columns[index], StringComparer.OrdinalIgnoreCase) && SmallCounts.AsNumber(row[index]) is { } hour and >= 0 and < 24)
        {
            return $"{(int)hour:00}:00";
        }

        return Format(row[index]) ?? "Not given";
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB")),
        DateTime time => time.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-GB")),
        bool b => b ? "Yes" : "No",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static int? Find(QueryResult result, string? name) => name is null ? null : Find(result, [name]);

    private static int? Find(QueryResult result, string[] names)
    {
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (names.Contains(result.Columns[i], StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}
