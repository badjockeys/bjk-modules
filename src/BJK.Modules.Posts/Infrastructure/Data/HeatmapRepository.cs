using System.Data;
using System.Text;
using BJK.Modules.Posts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Which incidents the heat map counts. Always public posts only, from their stored (rounded) pins.</summary>
/// <param name="Area">The box (already snapped to whole cells by the caller, for the cell counts).</param>
/// <param name="From">Incidents from this instant (inclusive).</param>
/// <param name="To">Up to this instant (exclusive).</param>
/// <param name="Categories">Only these kinds of incident (empty = all).</param>
/// <param name="Hours">Only this time of day, UK time.</param>
/// <param name="Days">Weekdays, weekends or all, UK time.</param>
public sealed record HeatmapFilter(
    GeoBox Area,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyCollection<IncidentCategory> Categories,
    HourRange? Hours,
    HeatmapDays Days);

/// <summary>A grid cell (indexes on <see cref="HeatmapGrid"/>) and its incident count.</summary>
public sealed class HeatmapCellRow
{
    public long CellY { get; set; }

    public long CellX { get; set; }

    public int Count { get; set; }
}

/// <summary>A count by key (hour, category or place name).</summary>
public sealed class HeatmapCountRow
{
    public string Key { get; set; } = string.Empty;

    public int Count { get; set; }
}

public interface IHeatmapRepository
{
    /// <summary>
    /// Incident counts per grid cell, busiest first, leaving out cells with fewer than <paramref name="minCount"/>.
    /// At most <paramref name="maxCells"/>.
    /// </summary>
    Task<IReadOnlyList<HeatmapCellRow>> CountCellsAsync(
        HeatmapFilter filter,
        decimal latitudeSize,
        decimal longitudeSize,
        int minCount,
        int maxCells,
        CancellationToken cancellationToken);

    /// <summary>How many incidents match.</summary>
    Task<int> CountAsync(HeatmapFilter filter, CancellationToken cancellationToken);

    /// <summary>Counts by UK hour of day (keys "0" to "23").</summary>
    Task<IReadOnlyList<HeatmapCountRow>> CountByHourAsync(HeatmapFilter filter, CancellationToken cancellationToken);

    /// <summary>Counts by category (keys are category names).</summary>
    Task<IReadOnlyList<HeatmapCountRow>> CountByCategoryAsync(HeatmapFilter filter, CancellationToken cancellationToken);

    /// <summary>The most common place names, busiest first (posts without one are left out).</summary>
    Task<IReadOnlyList<HeatmapCountRow>> CountByPlaceAsync(HeatmapFilter filter, int top, CancellationToken cancellationToken);
}

/// <summary>
/// The heat map's queries, in SQL. Every query starts from the filtered index <c>IX_Posts_Heatmap</c> (public posts by
/// incident time, including the pin, category and place name), with exactly its filter written out as literals so SQL
/// Server can use it: a period is one range seek and nothing else is read. Time of day and weekday are worked out in
/// UK time (<c>AT TIME ZONE 'GMT Standard Time'</c>, which follows British Summer Time).
/// </summary>
internal sealed class HeatmapRepository(PostsDbContext db) : IHeatmapRepository
{
    private const string UkTime = "([IncidentAt] AT TIME ZONE 'GMT Standard Time')";

    public async Task<IReadOnlyList<HeatmapCellRow>> CountCellsAsync(
        HeatmapFilter filter,
        decimal latitudeSize,
        decimal longitudeSize,
        int minCount,
        int maxCells,
        CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter);
        parameters.Add(Decimal("@latSize", latitudeSize));
        parameters.Add(Decimal("@lngSize", longitudeSize));
        parameters.Add(new SqlParameter("@minCount", SqlDbType.Int) { Value = minCount });
        parameters.Add(new SqlParameter("@maxCells", SqlDbType.Int) { Value = maxCells });
        var sql = $"""
            SELECT [CellY], [CellX], COUNT(*) AS [Count]
            FROM (
                SELECT CAST(FLOOR([Latitude] / @latSize + 0.5) AS bigint) AS [CellY],
                       CAST(FLOOR([Longitude] / @lngSize + 0.5) AS bigint) AS [CellX]
                FROM [posts].[Posts]
                WHERE {where}) AS [x]
            GROUP BY [CellY], [CellX]
            HAVING COUNT(*) >= @minCount
            ORDER BY COUNT(*) DESC, [CellY], [CellX]
            OFFSET 0 ROWS FETCH NEXT @maxCells ROWS ONLY
            """;
        return await db.Database.SqlQueryRaw<HeatmapCellRow>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(HeatmapFilter filter, CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter);
        var sql = "SELECT COUNT(*) AS [Value] FROM [posts].[Posts] WHERE " + where;
        var rows = await db.Database.SqlQueryRaw<int>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
        return rows.Count == 0 ? 0 : rows[0];
    }

    public Task<IReadOnlyList<HeatmapCountRow>> CountByHourAsync(HeatmapFilter filter, CancellationToken cancellationToken) =>
        CountByAsync(filter, $"CAST(DATEPART(hour, {UkTime}) AS nvarchar(40))", top: null, cancellationToken);

    public Task<IReadOnlyList<HeatmapCountRow>> CountByCategoryAsync(HeatmapFilter filter, CancellationToken cancellationToken) =>
        CountByAsync(filter, "[Category]", top: null, cancellationToken);

    public Task<IReadOnlyList<HeatmapCountRow>> CountByPlaceAsync(HeatmapFilter filter, int top, CancellationToken cancellationToken) =>
        CountByAsync(filter, "[PlaceName]", top, cancellationToken);

    private async Task<IReadOnlyList<HeatmapCountRow>> CountByAsync(
        HeatmapFilter filter,
        string key,
        int? top,
        CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter);
        var limit = top is { } n ? $"TOP ({n}) " : string.Empty;
        var sql = $"""
            SELECT {limit}[Key], COUNT(*) AS [Count]
            FROM (SELECT {key} AS [Key] FROM [posts].[Posts] WHERE {where}) AS [x]
            WHERE [Key] IS NOT NULL
            GROUP BY [Key]
            ORDER BY COUNT(*) DESC, [Key]
            """;
        return await db.Database.SqlQueryRaw<HeatmapCountRow>(sql, parameters.ToArray()).ToListAsync(cancellationToken);
    }

    /// <summary>The WHERE clause and its parameters (new ones each time: a parameter can only be in one command).</summary>
    private static (string Sql, List<SqlParameter> Parameters) Where(HeatmapFilter filter)
    {
        // Exactly IX_Posts_Heatmap's filter (PostsDbContext's PublicFilter), as literals.
        var sql = new StringBuilder(PostConfiguration.PublicFilter);
        var parameters = new List<SqlParameter>
        {
            new("@from", SqlDbType.DateTimeOffset) { Value = filter.From },
            new("@to", SqlDbType.DateTimeOffset) { Value = filter.To },
            Decimal("@minLat", filter.Area.MinLatitude),
            Decimal("@maxLat", filter.Area.MaxLatitude),
            Decimal("@minLng", filter.Area.MinLongitude),
            Decimal("@maxLng", filter.Area.MaxLongitude),
        };
        sql.Append(" AND [IncidentAt] >= @from AND [IncidentAt] < @to");
        sql.Append(" AND [Latitude] >= @minLat AND [Latitude] <= @maxLat AND [Longitude] >= @minLng AND [Longitude] <= @maxLng");

        if (filter.Categories.Count > 0)
        {
            var names = new List<string>();
            var i = 0;
            foreach (var category in filter.Categories.Distinct())
            {
                var name = "@c" + i++;
                names.Add(name);
                parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, 40) { Value = category.ToString() });
            }

            sql.Append(" AND [Category] IN (").Append(string.Join(", ", names)).Append(')');
        }

        if (filter.Hours is { } hours)
        {
            parameters.Add(new SqlParameter("@fromHour", SqlDbType.Int) { Value = hours.FromHour });
            parameters.Add(new SqlParameter("@toHour", SqlDbType.Int) { Value = hours.ToHour });
            sql.Append(hours.FromHour < hours.ToHour
                ? $" AND DATEPART(hour, {UkTime}) >= @fromHour AND DATEPART(hour, {UkTime}) < @toHour"
                : $" AND (DATEPART(hour, {UkTime}) >= @fromHour OR DATEPART(hour, {UkTime}) < @toHour)");
        }

        // Days since Monday 1 January 1900, modulo 7: 0 is Monday, 5 Saturday, 6 Sunday (independent of DATEFIRST).
        var weekday = $"(DATEDIFF(day, CAST('19000101' AS date), CAST({UkTime} AS date)) % 7)";
        switch (filter.Days)
        {
            case HeatmapDays.Weekdays:
                sql.Append($" AND {weekday} < 5");
                break;
            case HeatmapDays.Weekends:
                sql.Append($" AND {weekday} >= 5");
                break;
        }

        return (sql.ToString(), parameters);
    }

    private static SqlParameter Decimal(string name, decimal value) =>
        new(name, SqlDbType.Decimal) { Value = value, Precision = 18, Scale = 6 };
}
