using System.Globalization;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Heatmap;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Dashboard;

/// <summary>
/// One card on the dashboard, in order. <c>Key</c> identifies it for reordering: the built-in card's kind
/// (<c>CategoryTrends</c>, <c>Trending</c>, <c>WhoToFollow</c>) or the widget's id. Built-in cards can be
/// <c>Hidden</c>; widgets past the plan's limit (after a downgrade) are <c>Locked</c>: kept, but their figures aren't
/// given until the member upgrades or deletes others. A widget's place is only ever shown to its owner.
/// </summary>
public sealed record DashboardItemDto(
    string Key,
    DashboardKind Kind,
    bool BuiltIn,
    bool Hidden,
    bool Locked,
    Guid? Id,
    string? PlaceName,
    decimal? Latitude,
    decimal? Longitude,
    int? RadiusKm,
    int? PeriodDays,
    IReadOnlyList<IncidentCategory> Types,
    int Position);

/// <summary>
/// The signed-in member's dashboard: the right-hand panel on desktop, and the Dashboard tab on their profile on phones.
/// <c>Limit</c> is how many widgets their plan allows (Free 0, Premium 3, Premium + 5) and <c>Used</c> how many they
/// have; the built-in cards don't count.
/// </summary>
public sealed record DashboardDto(IReadOnlyList<DashboardItemDto> Items, int Limit, int Used, SubscriptionPlan Plan);

/// <summary>A widget's settings, as sent when adding, changing or previewing one.</summary>
public interface IDashboardWidgetRequest
{
    DashboardKind Kind { get; }

    string PlaceName { get; }

    decimal Latitude { get; }

    decimal Longitude { get; }

    int RadiusKm { get; }

    int PeriodDays { get; }

    IReadOnlyList<IncidentCategory>? Types { get; }
}

public abstract class DashboardWidgetRequestValidator<T> : AbstractValidator<T>
    where T : IDashboardWidgetRequest
{
    protected DashboardWidgetRequestValidator()
    {
        RuleFor(w => w.Kind)
            .IsInEnum()
            .Must(k => !DashboardItem.IsBuiltInKind(k))
            .WithMessage("Choose what the widget shows: AreaIncidents, TypeTrend, WhenItHappens or TopRoads.");
        RuleFor(w => w.PlaceName)
            .NotEmpty()
            .WithMessage("Choose a place: a postcode or a town.")
            .Must(p => p is null || p.Trim().Length <= DashboardItem.PlaceNameMaxLength)
            .WithMessage($"Place names are {DashboardItem.PlaceNameMaxLength} characters or fewer.");
        RuleFor(w => w.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(w => w.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(w => w.RadiusKm)
            .Must(r => DashboardItem.Radiuses.Contains(r))
            .WithMessage($"Choose a radius of {string.Join(", ", DashboardItem.Radiuses)} km.");
        RuleFor(w => w.PeriodDays)
            .Must(d => DashboardItem.Periods.Contains(d))
            .WithMessage($"Choose a period of {string.Join(", ", DashboardItem.Periods)} days.");
        RuleForEach(w => w.Types).IsInEnum();
        RuleFor(w => w.Types)
            .Must(t => t is null || t.Distinct().Count() <= DashboardItem.MaxTypes)
            .WithMessage($"Choose up to {DashboardItem.MaxTypes} types of bad driving.");
        RuleFor(w => w)
            .Must(w => w.Kind != DashboardKind.TypeTrend || w.Types is { } t && t.Distinct().Count() == 1)
            .OverridePropertyName("Types")
            .WithMessage("Choose the type of bad driving to follow.");
    }
}

internal static class DashboardLayout
{
    public static DashboardWidgetDetails ToDetails(this IDashboardWidgetRequest r) => new(
        r.Kind,
        r.PlaceName.Trim(),
        r.Latitude,
        r.Longitude,
        r.RadiusKm,
        r.PeriodDays,
        (r.Types ?? []).Distinct().Order().ToList());

    public static string KeyOf(DashboardItem item) => item.IsBuiltIn ? item.Kind.ToString() : item.Id.ToString();

    /// <summary>
    /// The member's cards in order: their saved rows, with any built-in card they have no row for added at the end
    /// (the default layout when they have none).
    /// </summary>
    public static IReadOnlyList<DashboardItemDto> Effective(IReadOnlyList<DashboardItem> rows, int limit)
    {
        var result = new List<DashboardItemDto>();
        var widgets = 0;
        foreach (var row in rows)
        {
            var locked = !row.IsBuiltIn && ++widgets > limit;
            result.Add(new DashboardItemDto(
                KeyOf(row),
                row.Kind,
                row.IsBuiltIn,
                row.Hidden,
                locked,
                row.IsBuiltIn ? null : row.Id,
                row.PlaceName,
                row.Latitude,
                row.Longitude,
                row.RadiusKm,
                row.PeriodDays,
                row.Types,
                result.Count));
        }

        foreach (var kind in DashboardItem.BuiltIns.Where(k => rows.All(r => r.Kind != k)))
        {
            result.Add(new DashboardItemDto(kind.ToString(), kind, true, false, false, null, null, null, null, null, null, [], result.Count));
        }

        return result;
    }

    /// <summary>
    /// Makes sure every built-in card has a row, so the member's layout can be saved. New rows go where the effective
    /// layout puts them (the end).
    /// </summary>
    public static IReadOnlyList<DashboardItem> Materialise(IDashboardRepository repository, Guid userId, IReadOnlyList<DashboardItem> rows, DateTimeOffset now)
    {
        var all = rows.ToList();
        var next = all.Count == 0 ? 0 : all.Max(r => r.Position) + 1;
        foreach (var kind in DashboardItem.BuiltIns.Where(k => rows.All(r => r.Kind != k)))
        {
            var item = DashboardItem.BuiltIn(userId, kind, next++, now);
            repository.Add(item);
            all.Add(item);
        }

        return all;
    }

    public static DashboardDto Dto(IReadOnlyList<DashboardItem> rows, SubscriptionPlan plan)
    {
        var limit = DashboardItem.LimitFor(plan);
        var ordered = rows.OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ToList();
        return new DashboardDto(Effective(ordered, limit), limit, ordered.Count(r => !r.IsBuiltIn), plan);
    }

    public static void RequirePremium(SubscriptionPlan plan)
    {
        if (DashboardItem.LimitFor(plan) == 0)
        {
            throw new ForbiddenException("Your own dashboard widgets are part of Premium.");
        }
    }
}

/// <summary>Your dashboard: the built-in cards and your widgets, in order, with your plan's limit.</summary>
public sealed record GetDashboardQuery : IRequest<DashboardDto>;

public sealed class GetDashboardQueryHandler(IDashboardRepository dashboard, IPlanResolver plans, ICurrentUser caller)
    : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<DashboardDto> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var rows = await dashboard.ListAsync(caller.RequireUserId(), cancellationToken);
        return DashboardLayout.Dto(rows, plans.CurrentPlan() ?? SubscriptionPlan.Free);
    }
}

/// <summary>Adds a widget at the end of your dashboard. Premium 3, Premium + 5; Free can't (403).</summary>
public sealed record CreateDashboardWidgetCommand(
    DashboardKind Kind,
    string PlaceName,
    decimal Latitude,
    decimal Longitude,
    int RadiusKm,
    int PeriodDays,
    IReadOnlyList<IncidentCategory>? Types = null) : IRequest<DashboardDto>, IDashboardWidgetRequest;

public sealed class CreateDashboardWidgetCommandValidator : DashboardWidgetRequestValidator<CreateDashboardWidgetCommand>
{
}

public sealed class CreateDashboardWidgetCommandHandler(
    IDashboardRepository dashboard,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CreateDashboardWidgetCommand, DashboardDto>
{
    public async Task<DashboardDto> Handle(CreateDashboardWidgetCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        DashboardLayout.RequirePremium(plan);
        var now = clock.GetUtcNow();
        var rows = await dashboard.ListAsync(userId, cancellationToken);
        var limit = DashboardItem.LimitFor(plan);
        if (rows.Count(r => !r.IsBuiltIn) >= limit)
        {
            throw PostAccess.Invalid(
                "Widgets",
                plan == SubscriptionPlan.PremiumPlus
                    ? $"You can have up to {limit} widgets. Delete one first."
                    : $"Your plan allows {limit} widgets. Delete one, or upgrade to Premium + for more.");
        }

        var all = DashboardLayout.Materialise(dashboard, userId, rows, now).ToList();
        var widget = DashboardItem.Widget(userId, request.ToDetails(), all.Max(r => r.Position) + 1, now);
        dashboard.Add(widget);
        all.Add(widget);
        await dashboard.SaveChangesAsync(cancellationToken);
        return DashboardLayout.Dto(all, plan);
    }
}

/// <summary>Changes one of your widgets.</summary>
public sealed record UpdateDashboardWidgetCommand(
    Guid Id,
    DashboardKind Kind,
    string PlaceName,
    decimal Latitude,
    decimal Longitude,
    int RadiusKm,
    int PeriodDays,
    IReadOnlyList<IncidentCategory>? Types = null) : IRequest<DashboardDto>, IDashboardWidgetRequest;

public sealed class UpdateDashboardWidgetCommandValidator : DashboardWidgetRequestValidator<UpdateDashboardWidgetCommand>
{
}

public sealed class UpdateDashboardWidgetCommandHandler(
    IDashboardRepository dashboard,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<UpdateDashboardWidgetCommand, DashboardDto>
{
    public async Task<DashboardDto> Handle(UpdateDashboardWidgetCommand request, CancellationToken cancellationToken)
    {
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        DashboardLayout.RequirePremium(plan);
        var rows = await dashboard.ListAsync(caller.RequireUserId(), cancellationToken);
        var widget = rows.FirstOrDefault(r => r.Id == request.Id && !r.IsBuiltIn)
            ?? throw new NotFoundException("Widget", request.Id);
        widget.Update(request.ToDetails(), clock.GetUtcNow());
        await dashboard.SaveChangesAsync(cancellationToken);
        return DashboardLayout.Dto(rows, plan);
    }
}

/// <summary>Deletes one of your widgets (any plan, so a downgraded member can tidy up). Nothing if it's already gone.</summary>
public sealed record DeleteDashboardWidgetCommand(Guid Id) : IRequest<DashboardDto>;

public sealed class DeleteDashboardWidgetCommandHandler(IDashboardRepository dashboard, IPlanResolver plans, ICurrentUser caller)
    : IRequestHandler<DeleteDashboardWidgetCommand, DashboardDto>
{
    public async Task<DashboardDto> Handle(DeleteDashboardWidgetCommand request, CancellationToken cancellationToken)
    {
        var rows = (await dashboard.ListAsync(caller.RequireUserId(), cancellationToken)).ToList();
        if (rows.FirstOrDefault(r => r.Id == request.Id && !r.IsBuiltIn) is { } widget)
        {
            dashboard.Remove(widget);
            rows.Remove(widget);
            await dashboard.SaveChangesAsync(cancellationToken);
        }

        return DashboardLayout.Dto(rows, plans.CurrentPlan() ?? SubscriptionPlan.Free);
    }
}

/// <summary>
/// Puts your dashboard in this order: every card's key (built-in cards by kind, widgets by id), each once. Any plan, so
/// visitors' default cards can be arranged by Free members too.
/// </summary>
public sealed record ReorderDashboardCommand(IReadOnlyList<string> Keys) : IRequest<DashboardDto>;

public sealed class ReorderDashboardCommandHandler(
    IDashboardRepository dashboard,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ReorderDashboardCommand, DashboardDto>
{
    public async Task<DashboardDto> Handle(ReorderDashboardCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var rows = DashboardLayout.Materialise(dashboard, userId, await dashboard.ListAsync(userId, cancellationToken), clock.GetUtcNow());
        var keys = request.Keys ?? [];
        var byKey = rows.ToDictionary(DashboardLayout.KeyOf, StringComparer.OrdinalIgnoreCase);
        if (keys.Count != rows.Count || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count || !keys.All(byKey.ContainsKey))
        {
            throw PostAccess.Invalid(nameof(request.Keys), "List each card on your dashboard once, in the new order.");
        }

        for (var i = 0; i < keys.Count; i++)
        {
            byKey[keys[i]].MoveTo(i);
        }

        await dashboard.SaveChangesAsync(cancellationToken);
        return DashboardLayout.Dto(rows, plans.CurrentPlan() ?? SubscriptionPlan.Free);
    }
}

/// <summary>Hides or shows one of the built-in cards (Category trends, Trending, Who to follow). Any plan.</summary>
public sealed record SetDashboardCardHiddenCommand(DashboardKind Kind, bool Hidden) : IRequest<DashboardDto>;

public sealed class SetDashboardCardHiddenCommandValidator : AbstractValidator<SetDashboardCardHiddenCommand>
{
    public SetDashboardCardHiddenCommandValidator() =>
        RuleFor(c => c.Kind)
            .Must(DashboardItem.IsBuiltInKind)
            .WithMessage("Only Category trends, Trending and Who to follow can be hidden. Delete widgets instead.");
}

public sealed class SetDashboardCardHiddenCommandHandler(
    IDashboardRepository dashboard,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SetDashboardCardHiddenCommand, DashboardDto>
{
    public async Task<DashboardDto> Handle(SetDashboardCardHiddenCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var now = clock.GetUtcNow();
        var rows = DashboardLayout.Materialise(dashboard, userId, await dashboard.ListAsync(userId, cancellationToken), now);
        rows.First(r => r.Kind == request.Kind).SetHidden(request.Hidden, now);
        await dashboard.SaveChangesAsync(cancellationToken);
        return DashboardLayout.Dto(rows, plans.CurrentPlan() ?? SubscriptionPlan.Free);
    }
}

/// <summary>Four hours of the day (UK time, <c>FromHour</c> inclusive to <c>ToHour</c> exclusive) and its incidents; null under the threshold.</summary>
public sealed record TimeBandDto(int FromHour, int ToHour, int? Count);

/// <summary>A day of the week and its incidents; null under the threshold.</summary>
public sealed record WeekdayCountDto(DayOfWeek Day, int? Count);

/// <summary>
/// A widget's figures for its place, radius and period (the last <c>PeriodDays</c> UK days, up to today). The same
/// privacy rules as the heat map: counted from public posts' rounded pins, and when fewer than <c>MinCount</c> (3)
/// incidents match, <c>Suppressed</c> is true and nothing else is given. Each kind fills in its own parts:
/// <list type="bullet">
/// <item><c>AreaIncidents</c>: <c>Total</c>, <c>ChangePercent</c> (on the same number of days before; null if that had
/// too few), <c>Counts</c> per period (<c>PeriodStarts</c>: days up to 92 days, then weeks) and <c>TopCategories</c>.</item>
/// <item><c>TypeTrend</c>: the same, for its one type, without <c>TopCategories</c>.</item>
/// <item><c>WhenItHappens</c>: <c>Total</c>, <c>TimesOfDay</c> (six 4-hour bands), <c>Weekdays</c> (Monday first) and
/// <c>BusiestHour</c>; bands and days under the threshold have a null count.</item>
/// <item><c>TopRoads</c>: <c>Total</c> and up to 5 <c>TopPlaces</c>, each with at least the threshold.</item>
/// </list>
/// </summary>
public sealed record DashboardWidgetDataDto(
    DashboardKind Kind,
    bool Suppressed,
    int MinCount,
    DateOnly From,
    DateOnly To,
    int? Total,
    int? ChangePercent,
    TrendPeriod Period,
    IReadOnlyList<DateOnly> PeriodStarts,
    IReadOnlyList<int> Counts,
    IReadOnlyList<CategoryShareDto> TopCategories,
    IReadOnlyList<PlaceCountDto> TopPlaces,
    IReadOnlyList<TimeBandDto> TimesOfDay,
    IReadOnlyList<WeekdayCountDto> Weekdays,
    int? BusiestHour);

/// <summary>Counts a widget's figures. Premium and Premium + only (the caller checks).</summary>
public sealed class DashboardFigures(IHeatmapRepository heatmap, IOptions<HeatmapOptions> options, TimeProvider clock)
{
    public const int TopPlaces = 5;

    public async Task<DashboardWidgetDataDto> CountAsync(DashboardWidgetDetails widget, CancellationToken cancellationToken)
    {
        var min = options.Value.MinCellCount;
        var to = UkCalendar.Today(clock.GetUtcNow());
        var from = to.AddDays(1 - widget.PeriodDays);
        var period = TrendPeriods.For(widget.PeriodDays);
        var box = GeoBox.Around(widget.Latitude, widget.Longitude, widget.RadiusKm);
        var filter = Filter(box, from, to, widget.Types);

        var total = await heatmap.CountAsync(filter, cancellationToken);
        if (total < min)
        {
            return Empty(widget.Kind, true, min, from, to, period);
        }

        var result = Empty(widget.Kind, false, min, from, to, period) with { Total = total };
        if (widget.Kind is DashboardKind.AreaIncidents or DashboardKind.TypeTrend)
        {
            var previousTo = from.AddDays(-1);
            var previous = await heatmap.CountAsync(Filter(box, previousTo.AddDays(1 - widget.PeriodDays), previousTo, widget.Types), cancellationToken);
            var rows = await heatmap.CountByPeriodAsync(filter, period, cancellationToken);
            var byStart = rows.ToDictionary(r => DateOnly.FromDateTime(r.Period), r => r.Count);
            var starts = TrendPeriods.Starts(from, to, period);
            result = result with
            {
                ChangePercent = previous >= min ? (int)Math.Round((total - previous) * 100m / previous, MidpointRounding.AwayFromZero) : null,
                PeriodStarts = starts,
                Counts = starts.Select(s => byStart.GetValueOrDefault(s)).ToList(),
            };
        }

        if (widget.Kind == DashboardKind.AreaIncidents)
        {
            result = result with
            {
                TopCategories = (await heatmap.CountByCategoryAsync(filter, cancellationToken))
                    .Where(c => c.Count >= min && Enum.TryParse<IncidentCategory>(c.Key, out _))
                    .Take(GetAreaStatsQueryHandler.Top)
                    .Select(c => new CategoryShareDto(
                        Enum.Parse<IncidentCategory>(c.Key),
                        c.Count,
                        (int)Math.Round(c.Count * 100m / total, MidpointRounding.AwayFromZero)))
                    .ToList(),
            };
        }

        if (widget.Kind == DashboardKind.WhenItHappens)
        {
            var hours = (await heatmap.CountByHourAsync(filter, cancellationToken))
                .ToDictionary(h => int.Parse(h.Key, CultureInfo.InvariantCulture), h => h.Count);
            var days = (await heatmap.CountByWeekdayAsync(filter, cancellationToken))
                .ToDictionary(d => int.Parse(d.Key, CultureInfo.InvariantCulture), d => d.Count);
            var busiest = hours.Where(h => h.Value >= min).OrderByDescending(h => h.Value).ThenBy(h => h.Key).Select(h => (int?)h.Key).FirstOrDefault();
            result = result with
            {
                TimesOfDay = Enumerable.Range(0, 6)
                    .Select(b =>
                    {
                        var count = Enumerable.Range(b * 4, 4).Sum(h => hours.GetValueOrDefault(h));
                        return new TimeBandDto(b * 4, (b * 4) + 4, count >= min ? count : null);
                    })
                    .ToList(),
                Weekdays = Enumerable.Range(0, 7)
                    .Select(d =>
                    {
                        var count = days.GetValueOrDefault(d);
                        return new WeekdayCountDto((DayOfWeek)((d + 1) % 7), count >= min ? count : null);
                    })
                    .ToList(),
                BusiestHour = busiest,
            };
        }

        if (widget.Kind == DashboardKind.TopRoads)
        {
            result = result with
            {
                TopPlaces = (await heatmap.CountByPlaceAsync(filter, TopPlaces, cancellationToken))
                    .Where(p => p.Count >= min && !string.IsNullOrWhiteSpace(p.Key))
                    .Select(p => new PlaceCountDto(p.Key, p.Count))
                    .ToList(),
            };
        }

        return result;
    }

    private static HeatmapFilter Filter(GeoBox box, DateOnly from, DateOnly to, IReadOnlyList<IncidentCategory> types) =>
        new(box, UkCalendar.StartOf(from), UkCalendar.StartOf(to.AddDays(1)), types, null, HeatmapDays.All);

    private static DashboardWidgetDataDto Empty(DashboardKind kind, bool suppressed, int min, DateOnly from, DateOnly to, TrendPeriod period) =>
        new(kind, suppressed, min, from, to, null, null, period, [], [], [], [], [], [], null);
}

/// <summary>One of your widgets' figures. Premium and Premium + only; a locked widget (past the limit) gets 403.</summary>
public sealed record GetDashboardWidgetDataQuery(Guid Id) : IRequest<DashboardWidgetDataDto>;

public sealed class GetDashboardWidgetDataQueryHandler(
    IDashboardRepository dashboard,
    DashboardFigures figures,
    IPlanResolver plans,
    ICurrentUser caller)
    : IRequestHandler<GetDashboardWidgetDataQuery, DashboardWidgetDataDto>
{
    public async Task<DashboardWidgetDataDto> Handle(GetDashboardWidgetDataQuery request, CancellationToken cancellationToken)
    {
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        DashboardLayout.RequirePremium(plan);
        var rows = await dashboard.ListAsync(caller.RequireUserId(), cancellationToken);
        var item = DashboardLayout.Effective(rows, DashboardItem.LimitFor(plan)).FirstOrDefault(i => i.Id == request.Id)
            ?? throw new NotFoundException("Widget", request.Id);
        if (item.Locked)
        {
            throw new ForbiddenException("Your plan allows fewer widgets than you have. Upgrade, or delete one to see the rest.");
        }

        var widget = rows.First(r => r.Id == request.Id).Details()!;
        return await figures.CountAsync(widget, cancellationToken);
    }
}

/// <summary>The figures a widget would show, before it's saved (the "Add a widget" preview). Premium and Premium + only.</summary>
public sealed record PreviewDashboardWidgetQuery(
    DashboardKind Kind,
    string PlaceName,
    decimal Latitude,
    decimal Longitude,
    int RadiusKm,
    int PeriodDays,
    IReadOnlyList<IncidentCategory>? Types = null) : IRequest<DashboardWidgetDataDto>, IDashboardWidgetRequest;

public sealed class PreviewDashboardWidgetQueryValidator : DashboardWidgetRequestValidator<PreviewDashboardWidgetQuery>
{
}

public sealed class PreviewDashboardWidgetQueryHandler(DashboardFigures figures, IPlanResolver plans)
    : IRequestHandler<PreviewDashboardWidgetQuery, DashboardWidgetDataDto>
{
    public async Task<DashboardWidgetDataDto> Handle(PreviewDashboardWidgetQuery request, CancellationToken cancellationToken)
    {
        DashboardLayout.RequirePremium(plans.CurrentPlan() ?? SubscriptionPlan.Free);
        return await figures.CountAsync(request.ToDetails(), cancellationToken);
    }
}
