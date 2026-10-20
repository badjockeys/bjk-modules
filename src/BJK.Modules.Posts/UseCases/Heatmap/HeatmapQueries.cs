using System.Globalization;
using System.Text;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Heatmap;

/// <summary>
/// The filters a heat map request asked for, as sent: <c>bbox</c> = <c>minLon,minLat,maxLon,maxLat</c>; dates are UK
/// calendar days (inclusive); <c>categories</c> comma-separated names; <c>hours</c> a UK time-of-day range such as
/// <c>7-10</c> (from 7:00 to 10:00) or <c>22-6</c>; <c>days</c> All, Weekdays or Weekends.
/// </summary>
public sealed record HeatmapRequest(
    string? Bbox,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Categories = null,
    string? Hours = null,
    HeatmapDays? Days = null)
{
    /// <summary>True if any filter beyond the area and dates was given.</summary>
    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Categories) || !string.IsNullOrWhiteSpace(Hours) || Days is { } d && d != HeatmapDays.All;

    /// <summary>Reads <c>minLon,minLat,maxLon,maxLat</c>. Null if missing or not valid.</summary>
    public static GeoBox? ParseBox(string? bbox)
    {
        var parts = bbox?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var values = new decimal[4];
        for (var i = 0; i < 4; i++)
        {
            if (!decimal.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        var (minLng, minLat, maxLng, maxLat) = (values[0], values[1], values[2], values[3]);
        return minLng is >= -180 and <= 180 && maxLng is >= -180 and <= 180 && minLat is >= -90 and <= 90 && maxLat is >= -90 and <= 90
            && minLng < maxLng && minLat < maxLat
            ? new GeoBox(minLat, maxLat, minLng, maxLng)
            : null;
    }

    /// <summary>Reads comma-separated category names (any case). Null if one isn't a category.</summary>
    public static IReadOnlyList<IncidentCategory>? ParseCategories(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
        {
            return [];
        }

        var result = new List<IncidentCategory>();
        foreach (var name in categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<IncidentCategory>(name, ignoreCase: true, out var category) || !Enum.IsDefined(category) || int.TryParse(name, out _))
            {
                return null;
            }

            result.Add(category);
        }

        return result;
    }
}

/// <summary>Validation shared by the heat map queries.</summary>
public static class HeatmapRequestRules
{
    public static void AddRules<T>(AbstractValidator<T> validator, Func<T, HeatmapRequest> request)
    {
        validator.RuleFor(q => request(q).Bbox)
            .Must(b => HeatmapRequest.ParseBox(b) is not null)
            .OverridePropertyName("bbox")
            .WithMessage("Give bbox as minLon,minLat,maxLon,maxLat, e.g. -2.9,53.3,-2.7,53.5.");
        validator.RuleFor(q => request(q).Categories)
            .Must(c => HeatmapRequest.ParseCategories(c) is not null)
            .OverridePropertyName("categories")
            .WithMessage("Give categories as comma-separated names, e.g. Tailgating,ClosePass.");
        validator.RuleFor(q => request(q).Hours)
            .Must(h => string.IsNullOrWhiteSpace(h) || HourRange.Parse(h) is not null)
            .OverridePropertyName("hours")
            .WithMessage("Give hours as a UK time range, e.g. 7-10 or 22-6.");
        validator.RuleFor(q => request(q).Days).IsInEnum().OverridePropertyName("days");
        validator.RuleFor(q => request(q))
            .Must(r => r.From is null || r.To is null || r.From <= r.To)
            .OverridePropertyName("from")
            .WithMessage("from must be on or before to.");
    }
}

/// <summary>
/// What the caller's plan allows and what was applied: the dates used (UK calendar days, inclusive), whether the
/// requested start was moved later to fit the plan's history (<c>HistoryCapped</c>), and whether filters were ignored
/// (Free: the national teaser, last 7 days, no filters).
/// </summary>
public sealed record HeatmapAccessDto(
    SubscriptionPlan Plan,
    int HistoryDays,
    bool FiltersAllowed,
    bool ExportAllowed,
    DateOnly From,
    DateOnly To,
    bool HistoryCapped,
    bool FiltersIgnored);

/// <summary>
/// The heat map's entitlements by plan: Free the last <c>Heatmap:HistoryDays:Free</c> (7) days with no filters;
/// Premium <c>:Premium</c> (365) days and filters; Premium + <c>:PremiumPlus</c> (1826) days, filters and CSV export.
/// </summary>
public sealed class HeatmapAccess(IPlanResolver plans, IOptions<HeatmapOptions> options, TimeProvider clock)
{
    /// <summary>The caller's plan (Free if the token has none).</summary>
    public SubscriptionPlan Plan => plans.CurrentPlan() ?? SubscriptionPlan.Free;

    /// <summary>The dates and filters to apply for this request.</summary>
    /// <param name="request">What was asked for.</param>
    public (HeatmapAccessDto Access, HeatmapRequest Applied) Resolve(HeatmapRequest request)
    {
        var plan = Plan;
        var days = options.Value.HistoryDays;
        var historyDays = plan switch
        {
            SubscriptionPlan.PremiumPlus => days.PremiumPlus,
            SubscriptionPlan.Premium => days.Premium,
            _ => days.Free,
        };
        var filtersAllowed = plan != SubscriptionPlan.Free;
        var today = UkCalendar.Today(clock.GetUtcNow());
        var earliest = today.AddDays(1 - historyDays);

        DateOnly from, to;
        var capped = false;
        if (!filtersAllowed)
        {
            // The teaser: always the latest days.
            (from, to) = (earliest, today);
        }
        else
        {
            to = request.To is { } t && t < today ? t : today;
            from = request.From ?? earliest;
            if (from < earliest)
            {
                (from, capped) = (earliest, true);
            }

            if (from > to)
            {
                from = to;
            }
        }

        var applied = filtersAllowed
            ? request with { From = from, To = to }
            : new HeatmapRequest(request.Bbox, from, to);
        return (
            new HeatmapAccessDto(plan, historyDays, filtersAllowed, plan == SubscriptionPlan.PremiumPlus, from, to, capped, !filtersAllowed && request.HasFilters),
            applied);
    }

    /// <summary>The repository filter for an applied request, over a box.</summary>
    /// <param name="applied">From <see cref="Resolve"/>.</param>
    /// <param name="area">The box to count in.</param>
    public static HeatmapFilter Filter(HeatmapRequest applied, GeoBox area) => Filter(applied, area, applied.From!.Value, applied.To!.Value);

    /// <summary>The repository filter for an applied request, over a box and dates.</summary>
    /// <param name="applied">From <see cref="Resolve"/>.</param>
    /// <param name="area">The box to count in.</param>
    /// <param name="from">First UK day.</param>
    /// <param name="to">Last UK day (inclusive).</param>
    public static HeatmapFilter Filter(HeatmapRequest applied, GeoBox area, DateOnly from, DateOnly to) => new(
        area,
        UkCalendar.StartOf(from),
        UkCalendar.StartOf(to.AddDays(1)),
        HeatmapRequest.ParseCategories(applied.Categories) ?? [],
        HourRange.Parse(applied.Hours),
        applied.Days ?? HeatmapDays.All);
}

/// <summary>One cell: its centre, bounds and incident count (always at least <c>Heatmap:MinCellCount</c>).</summary>
public sealed record HeatmapCellDto(
    decimal Latitude,
    decimal Longitude,
    decimal MinLatitude,
    decimal MinLongitude,
    decimal MaxLatitude,
    decimal MaxLongitude,
    int Count);

/// <summary>
/// The heat map: cells with counts, the cell size in degrees, the suppression threshold, the busiest cell's count (for
/// shading), whether cells were cut off at <c>Heatmap:MaxCells</c>, and what the plan allowed.
/// </summary>
public sealed record HeatmapDto(
    IReadOnlyList<HeatmapCellDto> Cells,
    decimal CellLatitudeSize,
    decimal CellLongitudeSize,
    int MinCellCount,
    int MaxCount,
    bool Truncated,
    HeatmapAccessDto Access);

/// <summary>
/// The incident heat map (signed-in users): incidents per grid cell in a box, counted only from public posts' stored
/// pins (rounded to about 500 m), sensitive posts included (counts reveal nothing about them). Cells with fewer than
/// <c>Heatmap:MinCellCount</c> (3) incidents are left out. Cell size follows the map's zoom (see
/// <see cref="HeatmapGrid"/>). Dates and filters depend on the plan (see <see cref="HeatmapAccess"/>).
/// </summary>
public sealed record GetHeatmapQuery(HeatmapRequest Request, int Zoom) : IRequest<HeatmapDto>;

public sealed class GetHeatmapQueryValidator : AbstractValidator<GetHeatmapQuery>
{
    public GetHeatmapQueryValidator()
    {
        RuleFor(q => q.Zoom).InclusiveBetween(0, 22);
        HeatmapRequestRules.AddRules(this, q => q.Request);
    }
}

public sealed class GetHeatmapQueryHandler(IHeatmapRepository heatmap, HeatmapAccess access, IOptions<HeatmapOptions> options)
    : IRequestHandler<GetHeatmapQuery, HeatmapDto>
{
    public async Task<HeatmapDto> Handle(GetHeatmapQuery request, CancellationToken cancellationToken)
    {
        var (allowed, applied) = access.Resolve(request.Request);
        var settings = options.Value;
        var cells = await HeatmapCells.CountAsync(heatmap, applied, request.Zoom, settings, cancellationToken);
        return new HeatmapDto(
            cells.Cells,
            HeatmapGrid.LatitudeSize(request.Zoom),
            HeatmapGrid.LongitudeSize(request.Zoom),
            settings.MinCellCount,
            cells.Cells.Count == 0 ? 0 : cells.Cells.Max(c => c.Count),
            cells.Truncated,
            allowed);
    }
}

/// <summary>Premium +: the same cells as <see cref="GetHeatmapQuery"/>, as CSV (aggregated counts only). Others get 403.</summary>
public sealed record ExportHeatmapQuery(HeatmapRequest Request, int Zoom) : IRequest<string>;

public sealed class ExportHeatmapQueryValidator : AbstractValidator<ExportHeatmapQuery>
{
    public ExportHeatmapQueryValidator()
    {
        RuleFor(q => q.Zoom).InclusiveBetween(0, 22);
        HeatmapRequestRules.AddRules(this, q => q.Request);
    }
}

public sealed class ExportHeatmapQueryHandler(IHeatmapRepository heatmap, HeatmapAccess access, IOptions<HeatmapOptions> options)
    : IRequestHandler<ExportHeatmapQuery, string>
{
    public async Task<string> Handle(ExportHeatmapQuery request, CancellationToken cancellationToken)
    {
        var (allowed, applied) = access.Resolve(request.Request);
        if (!allowed.ExportAllowed)
        {
            throw new ForbiddenException("CSV export is part of Premium +.");
        }

        var cells = await HeatmapCells.CountAsync(heatmap, applied, request.Zoom, options.Value, cancellationToken);
        var csv = new StringBuilder();
        csv.Append("from,to,centre_latitude,centre_longitude,min_latitude,min_longitude,max_latitude,max_longitude,incidents\n");
        foreach (var c in cells.Cells)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{allowed.From:yyyy-MM-dd},{allowed.To:yyyy-MM-dd},{c.Latitude},{c.Longitude},{c.MinLatitude},{c.MinLongitude},{c.MaxLatitude},{c.MaxLongitude},{c.Count}\n");
        }

        return csv.ToString();
    }
}

internal static class HeatmapCells
{
    public static async Task<(IReadOnlyList<HeatmapCellDto> Cells, bool Truncated)> CountAsync(
        IHeatmapRepository heatmap,
        HeatmapRequest applied,
        int zoom,
        HeatmapOptions settings,
        CancellationToken cancellationToken)
    {
        var box = HeatmapRequest.ParseBox(applied.Bbox)!;
        var latSize = HeatmapGrid.LatitudeSize(zoom);
        var lngSize = HeatmapGrid.LongitudeSize(zoom);

        // Widen the box to whole cells, so cells at the edge count every incident in them, not just the part in view.
        var (minY, maxY) = (HeatmapGrid.CellIndex(box.MinLatitude, latSize), HeatmapGrid.CellIndex(box.MaxLatitude, latSize));
        var (minX, maxX) = (HeatmapGrid.CellIndex(box.MinLongitude, lngSize), HeatmapGrid.CellIndex(box.MaxLongitude, lngSize));
        var snapped = new GeoBox(
            Math.Max(-90m, HeatmapGrid.Bounds(minY, latSize).Min),
            Math.Min(90m, HeatmapGrid.Bounds(maxY, latSize).Max),
            Math.Max(-180m, HeatmapGrid.Bounds(minX, lngSize).Min),
            Math.Min(180m, HeatmapGrid.Bounds(maxX, lngSize).Max));

        var rows = await heatmap.CountCellsAsync(
            HeatmapAccess.Filter(applied, snapped),
            latSize,
            lngSize,
            settings.MinCellCount,
            settings.MaxCells + 1,
            cancellationToken);
        var cells = rows
            .Take(settings.MaxCells)
            .Where(r => r.Count >= settings.MinCellCount)
            .Select(r =>
            {
                var (minLat, maxLat) = HeatmapGrid.Bounds(r.CellY, latSize);
                var (minLng, maxLng) = HeatmapGrid.Bounds(r.CellX, lngSize);
                return new HeatmapCellDto(
                    decimal.Round(r.CellY * latSize, 6),
                    decimal.Round(r.CellX * lngSize, 6),
                    minLat,
                    minLng,
                    maxLat,
                    maxLng,
                    r.Count);
            })
            .ToList();
        return (cells, rows.Count > settings.MaxCells);
    }
}

/// <summary>
/// How many of an area's incidents have this type of bad driving, and what percentage that is. A post can have several
/// types, so the percentages can add up to more than 100.
/// </summary>
public sealed record CategoryShareDto(IncidentCategory Category, int Count, int Percent);

/// <summary>A place name (usually a road and town, e.g. "A57 Warrington Road, Prescot") and its incidents.</summary>
public sealed record PlaceCountDto(string Name, int Count);

/// <summary>
/// An area's figures for the heat map's panel. When fewer than <c>Heatmap:MinCellCount</c> incidents match,
/// <c>Suppressed</c> is true and only the dates are filled in. <c>ChangePercent</c> compares with the same number of
/// days just before (null if that period had too few to compare). <c>BusiestHour</c> is the UK hour (0–23) starting the
/// busiest one-hour band. <c>TopCategories</c> and <c>TopPlaces</c> (from the place names stored with posts) list at most
/// 3 each, each with at least the threshold; <c>PlaceName</c> is the most common place, to title the panel.
/// </summary>
public sealed record AreaStatsDto(
    bool Suppressed,
    int? Total,
    int? ChangePercent,
    int? BusiestHour,
    IReadOnlyList<CategoryShareDto> TopCategories,
    IReadOnlyList<PlaceCountDto> TopPlaces,
    string? PlaceName,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    HeatmapAccessDto Access);

/// <summary>Figures for an area of the heat map (signed-in users). Same entitlements and suppression as the map.</summary>
public sealed record GetAreaStatsQuery(HeatmapRequest Request) : IRequest<AreaStatsDto>;

public sealed class GetAreaStatsQueryValidator : AbstractValidator<GetAreaStatsQuery>
{
    public GetAreaStatsQueryValidator() => HeatmapRequestRules.AddRules(this, q => q.Request);
}

public sealed class GetAreaStatsQueryHandler(IHeatmapRepository heatmap, HeatmapAccess access, IOptions<HeatmapOptions> options)
    : IRequestHandler<GetAreaStatsQuery, AreaStatsDto>
{
    public const int Top = 3;

    public async Task<AreaStatsDto> Handle(GetAreaStatsQuery request, CancellationToken cancellationToken)
    {
        var (allowed, applied) = access.Resolve(request.Request);
        var min = options.Value.MinCellCount;
        var box = HeatmapRequest.ParseBox(applied.Bbox)!;
        var length = allowed.To.DayNumber - allowed.From.DayNumber + 1;
        var previousTo = allowed.From.AddDays(-1);
        var previousFrom = previousTo.AddDays(1 - length);

        var filter = HeatmapAccess.Filter(applied, box);
        var total = await heatmap.CountAsync(filter, cancellationToken);
        if (total < min)
        {
            return new AreaStatsDto(true, null, null, null, [], [], null, previousFrom, previousTo, allowed);
        }

        var previous = await heatmap.CountAsync(HeatmapAccess.Filter(applied, box, previousFrom, previousTo), cancellationToken);
        int? change = previous >= min ? (int)Math.Round((total - previous) * 100m / previous, MidpointRounding.AwayFromZero) : null;

        var hours = await heatmap.CountByHourAsync(filter, cancellationToken);
        var busiest = hours.Where(h => h.Count >= min).OrderByDescending(h => h.Count).ThenBy(h => int.Parse(h.Key, CultureInfo.InvariantCulture)).FirstOrDefault();

        var categories = (await heatmap.CountByCategoryAsync(filter, cancellationToken))
            .Where(c => c.Count >= min && Enum.TryParse<IncidentCategory>(c.Key, out _))
            .Take(Top)
            .Select(c => new CategoryShareDto(
                Enum.Parse<IncidentCategory>(c.Key),
                c.Count,
                (int)Math.Round(c.Count * 100m / total, MidpointRounding.AwayFromZero)))
            .ToList();

        var places = (await heatmap.CountByPlaceAsync(filter, Top, cancellationToken))
            .Where(p => p.Count >= min && !string.IsNullOrWhiteSpace(p.Key))
            .Select(p => new PlaceCountDto(p.Key, p.Count))
            .ToList();

        return new AreaStatsDto(
            false,
            total,
            change,
            busiest is null ? null : int.Parse(busiest.Key, CultureInfo.InvariantCulture),
            categories,
            places,
            places.FirstOrDefault()?.Name,
            previousFrom,
            previousTo,
            allowed);
    }
}
