using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Heatmap;

/// <summary>How the trend lines are bucketed: days up to 92 days, weeks (from Monday) up to two years, then months.</summary>
public enum TrendPeriod
{
    Day,
    Week,
    Month,
}

/// <summary>
/// One type of bad driving's trend: its incidents in each period (aligned with <see cref="CategoryTrendsDto.Periods"/>),
/// the total, the total in the previous period of the same length, and the change as a percentage (null when the previous
/// period had too few to compare). When the total is below <c>Heatmap:MinCellCount</c> the type is <c>Suppressed</c>:
/// no figures at all.
/// </summary>
public sealed record CategoryTrendDto(
    IncidentCategory Category,
    bool Suppressed,
    int? Total,
    int? PreviousTotal,
    int? ChangePercent,
    IReadOnlyList<int> Points);

/// <summary>
/// The right panel's category trends: every type of bad driving, busiest first, with a line of incidents per
/// <see cref="Period"/> (each starting on the date in <see cref="Periods"/>), over <c>Access.From</c>–<c>Access.To</c>
/// (UK days, inclusive), compared with <see cref="PreviousFrom"/>–<see cref="PreviousTo"/>. <see cref="AreaAllowed"/>
/// is true for Premium and Premium +, who can narrow it to a box; <see cref="AreaIgnored"/> says a box was sent but
/// the whole UK was used.
/// </summary>
public sealed record CategoryTrendsDto(
    IReadOnlyList<CategoryTrendDto> Categories,
    TrendPeriod Period,
    IReadOnlyList<DateOnly> Periods,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    bool AreaAllowed,
    bool AreaIgnored,
    HeatmapAccessDto Access);

/// <summary>
/// Category trends for everyone (signed out too). The dates follow the heat map's plan limits: Free the last 7 days,
/// Premium up to 12 months, Premium + up to 5 years. Everyone sees the whole UK; Premium and Premium + can pass a box
/// (<c>bbox</c>) to see an area. Counted from public posts only, a post under each of its types.
/// </summary>
public sealed record GetCategoryTrendsQuery(string? Bbox, DateOnly? From, DateOnly? To) : IRequest<CategoryTrendsDto>;

public sealed class GetCategoryTrendsQueryValidator : AbstractValidator<GetCategoryTrendsQuery>
{
    public GetCategoryTrendsQueryValidator()
    {
        RuleFor(q => q.Bbox)
            .Must(b => string.IsNullOrWhiteSpace(b) || HeatmapRequest.ParseBox(b) is not null)
            .OverridePropertyName("bbox")
            .WithMessage("Give bbox as minLon,minLat,maxLon,maxLat, e.g. -2.9,53.3,-2.7,53.5.");
        RuleFor(q => q)
            .Must(q => q.From is null || q.To is null || q.From <= q.To)
            .OverridePropertyName("from")
            .WithMessage("from must be on or before to.");
    }
}

public sealed class GetCategoryTrendsQueryHandler(IHeatmapRepository heatmap, HeatmapAccess access, IOptions<HeatmapOptions> options)
    : IRequestHandler<GetCategoryTrendsQuery, CategoryTrendsDto>
{
    /// <summary>Everywhere: posts can only be pinned inside the service regions, so this is the whole UK.</summary>
    internal static readonly GeoBox Everywhere = new(-90m, 90m, -180m, 180m);

    public async Task<CategoryTrendsDto> Handle(GetCategoryTrendsQuery request, CancellationToken cancellationToken)
    {
        var wantsArea = !string.IsNullOrWhiteSpace(request.Bbox);
        var (allowed, _) = access.Resolve(new HeatmapRequest(request.Bbox, request.From, request.To));
        var areaAllowed = allowed.FiltersAllowed;
        var box = areaAllowed && wantsArea ? HeatmapRequest.ParseBox(request.Bbox)! : Everywhere;
        var min = options.Value.MinCellCount;

        var length = allowed.To.DayNumber - allowed.From.DayNumber + 1;
        var previousTo = allowed.From.AddDays(-1);
        var previousFrom = previousTo.AddDays(1 - length);
        var period = TrendPeriods.For(length);
        var periods = TrendPeriods.Starts(allowed.From, allowed.To, period);

        var all = new HeatmapRequest(null);
        var rows = await heatmap.CountByCategoryAndPeriodAsync(HeatmapAccess.Filter(all, box, allowed.From, allowed.To), period, cancellationToken);
        var previous = (await heatmap.CountByCategoryAsync(HeatmapAccess.Filter(all, box, previousFrom, previousTo), cancellationToken))
            .ToDictionary(r => r.Key, r => r.Count);

        var index = periods.Select((start, i) => (start, i)).ToDictionary(p => p.start, p => p.i);
        var trends = Enum.GetValues<IncidentCategory>()
            .Select(category =>
            {
                var points = new int[periods.Count];
                foreach (var row in rows.Where(r => r.Key == category.ToString()))
                {
                    if (index.TryGetValue(DateOnly.FromDateTime(row.Period), out var i))
                    {
                        points[i] += row.Count;
                    }
                }

                var total = points.Sum();
                if (total < min)
                {
                    return new CategoryTrendDto(category, true, null, null, null, []);
                }

                var before = previous.GetValueOrDefault(category.ToString());
                int? change = before >= min
                    ? (int)Math.Round((total - before) * 100m / before, MidpointRounding.AwayFromZero)
                    : null;
                return new CategoryTrendDto(category, false, total, before >= min ? before : null, change, points);
            })
            .OrderBy(t => t.Suppressed)
            .ThenByDescending(t => t.Total ?? 0)
            .ThenBy(t => t.Category)
            .ToList();

        return new CategoryTrendsDto(trends, period, periods, previousFrom, previousTo, areaAllowed, wantsArea && !areaAllowed, allowed);
    }
}

/// <summary>The periods a trend line is counted in.</summary>
public static class TrendPeriods
{
    /// <summary>Days up to 92 days; weeks up to 731 days (two years); months beyond.</summary>
    /// <param name="days">How many days the range covers.</param>
    public static TrendPeriod For(int days) => days <= 92 ? TrendPeriod.Day : days <= 731 ? TrendPeriod.Week : TrendPeriod.Month;

    /// <summary>The first day of the period a day falls in (weeks start on Monday).</summary>
    /// <param name="day">The day.</param>
    /// <param name="period">The period.</param>
    public static DateOnly StartOf(DateOnly day, TrendPeriod period) => period switch
    {
        TrendPeriod.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        TrendPeriod.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    /// <summary>The start of every period from <paramref name="from"/> to <paramref name="to"/>, in order.</summary>
    /// <param name="from">First day.</param>
    /// <param name="to">Last day (inclusive).</param>
    /// <param name="period">The period.</param>
    public static IReadOnlyList<DateOnly> Starts(DateOnly from, DateOnly to, TrendPeriod period)
    {
        var starts = new List<DateOnly>();
        for (var start = StartOf(from, period); start <= to; start = Next(start, period))
        {
            starts.Add(start);
        }

        return starts;
    }

    private static DateOnly Next(DateOnly start, TrendPeriod period) => period switch
    {
        TrendPeriod.Week => start.AddDays(7),
        TrendPeriod.Month => start.AddMonths(1),
        _ => start.AddDays(1),
    };
}
