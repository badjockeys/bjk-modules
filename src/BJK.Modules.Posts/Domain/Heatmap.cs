namespace BJK.Modules.Posts.Domain;

/// <summary>
/// The heat map's grid. Cells are fixed latitude/longitude squares on a worldwide grid (so they don't move as the map
/// pans), sized by zoom from about 500 m (the stored pins' own rounding) to about 10 km. A cell's longitude size is
/// <see cref="LongitudeFactor"/> times its latitude size, which makes cells roughly square across the UK (at 51–55°N,
/// where a degree of longitude is about 0.6 of a degree of latitude). Pins are counted in the cell whose centre is
/// nearest: cell index = floor(coordinate / size + 0.5).
/// </summary>
public static class HeatmapGrid
{
    /// <summary>Longitude cell size ÷ latitude cell size.</summary>
    public const decimal LongitudeFactor = 1.6m;

    /// <summary>The smallest cell: the 500 m the pins are rounded to (<see cref="PinRounding.LatitudeStep"/>).</summary>
    public const decimal FinestLatitudeSize = PinRounding.LatitudeStep;

    /// <summary>
    /// Latitude size of a cell, in degrees, for a web map zoom level (0 = the world, 18 = a street): about 500 m from
    /// zoom 14, 1 km at 13, 2 km at 12, 4 km at 11, 5 km at 10 and 10 km at 9 or less.
    /// </summary>
    /// <param name="zoom">The map's zoom level.</param>
    public static decimal LatitudeSize(int zoom) => zoom switch
    {
        >= 14 => FinestLatitudeSize,
        13 => FinestLatitudeSize * 2,
        12 => FinestLatitudeSize * 4,
        11 => FinestLatitudeSize * 8,
        10 => 0.045m,
        _ => 0.09m,
    };

    /// <summary>Longitude size of a cell, in degrees.</summary>
    /// <param name="zoom">The map's zoom level.</param>
    public static decimal LongitudeSize(int zoom) => LatitudeSize(zoom) * LongitudeFactor;

    /// <summary>Which cell a coordinate falls in.</summary>
    /// <param name="value">Latitude or longitude.</param>
    /// <param name="size">The cell size for that axis.</param>
    public static long CellIndex(decimal value, decimal size) => (long)Math.Floor((value / size) + 0.5m);

    /// <summary>The bounds of a cell on one axis.</summary>
    /// <param name="index">The cell index.</param>
    /// <param name="size">The cell size.</param>
    public static (decimal Min, decimal Max) Bounds(long index, decimal size) =>
        (decimal.Round((index - 0.5m) * size, 6), decimal.Round((index + 0.5m) * size, 6));
}

/// <summary>Which days of the week the heat map counts.</summary>
public enum HeatmapDays
{
    All,

    /// <summary>Monday to Friday (UK time).</summary>
    Weekdays,

    /// <summary>Saturday and Sunday (UK time).</summary>
    Weekends,
}

/// <summary>
/// A time-of-day filter: incidents from <see cref="FromHour"/> (inclusive) to <see cref="ToHour"/> (exclusive), UK
/// time. Wraps past midnight when <see cref="FromHour"/> is later than <see cref="ToHour"/> (e.g. 22–6).
/// </summary>
/// <param name="FromHour">0 to 23.</param>
/// <param name="ToHour">1 to 24.</param>
public sealed record HourRange(int FromHour, int ToHour)
{
    /// <summary>Reads <c>"7-10"</c>. Null if empty or not valid.</summary>
    /// <param name="value">The query value.</param>
    public static HourRange? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out var from)
            && int.TryParse(parts[1], out var to)
            && from is >= 0 and <= 23
            && to is >= 1 and <= 24
            && from != to
            ? new HourRange(from, to)
            : null;
    }

    /// <summary>True if the hour (0–23) is in the range.</summary>
    /// <param name="hour">The hour.</param>
    public bool Contains(int hour) => FromHour < ToHour ? hour >= FromHour && hour < ToHour : hour >= FromHour || hour < ToHour;
}
