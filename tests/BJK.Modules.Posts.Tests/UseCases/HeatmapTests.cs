using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Heatmap;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class HeatmapTests
{
    private const string Prescot = "-2.85,53.40,-2.75,53.45";

    private readonly IHeatmapRepository _heatmap = Substitute.For<IHeatmapRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly IOptions<HeatmapOptions> _options = Options.Create(new HeatmapOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HeatmapAccess Access(SubscriptionPlan plan)
    {
        _plans.CurrentPlan().Returns(plan);
        return new HeatmapAccess(_plans, _options, new FixedTimeProvider(Now));
    }

    [Fact]
    public void Free_gets_the_last_7_days_and_no_filters()
    {
        var (access, applied) = Access(SubscriptionPlan.Free).Resolve(
            new HeatmapRequest(Prescot, new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 1), "Tailgating", "7-10", HeatmapDays.Weekdays));

        Assert.Equal(new DateOnly(2026, 10, 12), access.From);
        Assert.Equal(new DateOnly(2026, 10, 18), access.To);
        Assert.False(access.FiltersAllowed);
        Assert.True(access.FiltersIgnored);
        Assert.False(access.ExportAllowed);
        Assert.Null(applied.Categories);
        Assert.Null(applied.Hours);
        Assert.Null(applied.Days);
    }

    [Fact]
    public void Premium_gets_12_months_and_filters_and_earlier_starts_are_capped()
    {
        var (access, applied) = Access(SubscriptionPlan.Premium).Resolve(
            new HeatmapRequest(Prescot, new DateOnly(2024, 1, 1), null, "Tailgating", "7-10", HeatmapDays.Weekdays));

        Assert.Equal(new DateOnly(2025, 10, 19), access.From);
        Assert.Equal(new DateOnly(2026, 10, 18), access.To);
        Assert.True(access.HistoryCapped);
        Assert.True(access.FiltersAllowed);
        Assert.False(access.FiltersIgnored);
        Assert.Equal("Tailgating", applied.Categories);

        var plus = Access(SubscriptionPlan.PremiumPlus).Resolve(new HeatmapRequest(Prescot, new DateOnly(2024, 1, 1)));
        Assert.False(plus.Access.HistoryCapped);
        Assert.Equal(new DateOnly(2024, 1, 1), plus.Access.From);
        Assert.True(plus.Access.ExportAllowed);
    }

    [Fact]
    public async Task Cells_below_the_threshold_are_dropped_and_the_box_is_widened_to_whole_cells()
    {
        _heatmap.CountCellsAsync(Arg.Any<HeatmapFilter>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Is(3), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCellRow>
            {
                new() { CellY = 11867, CellX = -396, Count = 12 },
                new() { CellY = 11868, CellX = -396, Count = 2 },
            });
        var handler = new GetHeatmapQueryHandler(_heatmap, Access(SubscriptionPlan.Premium), _options);

        var map = await handler.Handle(new GetHeatmapQuery(new HeatmapRequest(Prescot), Zoom: 14), Ct);

        var cell = Assert.Single(map.Cells);
        Assert.Equal(12, cell.Count);
        Assert.Equal(12, map.MaxCount);
        Assert.Equal(0.0045m, map.CellLatitudeSize);
        Assert.Equal(0.0072m, map.CellLongitudeSize);
        Assert.Equal(decimal.Round(11867 * 0.0045m, 6), cell.Latitude);
        Assert.True(cell.MinLatitude < cell.Latitude && cell.Latitude < cell.MaxLatitude);
        await _heatmap.Received().CountCellsAsync(
            Arg.Is<HeatmapFilter>(f => f.Area.MinLatitude <= 53.40m && f.Area.MaxLatitude >= 53.45m && f.Area.MinLongitude <= -2.85m),
            0.0045m,
            0.0072m,
            3,
            10_001,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Area_figures_are_hidden_below_the_threshold()
    {
        _heatmap.CountAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>()).Returns(2);
        var handler = new GetAreaStatsQueryHandler(_heatmap, Access(SubscriptionPlan.Premium), _options);

        var stats = await handler.Handle(new GetAreaStatsQuery(new HeatmapRequest(Prescot)), Ct);

        Assert.True(stats.Suppressed);
        Assert.Null(stats.Total);
        Assert.Empty(stats.TopCategories);
        await _heatmap.DidNotReceive().CountByCategoryAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Area_figures_compare_with_the_previous_period_and_hide_small_groups()
    {
        var from = new DateOnly(2026, 10, 1);
        _heatmap.CountAsync(Arg.Is<HeatmapFilter>(f => f.From == UkCalendar.StartOf(from)), Arg.Any<CancellationToken>()).Returns(38);
        _heatmap.CountAsync(Arg.Is<HeatmapFilter>(f => f.From < UkCalendar.StartOf(from)), Arg.Any<CancellationToken>()).Returns(34);
        _heatmap.CountByHourAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow> { new() { Key = "8", Count = 9 }, new() { Key = "17", Count = 7 } });
        _heatmap.CountByCategoryAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow>
            {
                new() { Key = "DoubleYellows", Count = 13 },
                new() { Key = "Tailgating", Count = 9 },
                new() { Key = "ClosePassCyclist", Count = 6 },
                new() { Key = "SpeedingInTown", Count = 2 },
            });
        _heatmap.CountByPlaceAsync(Arg.Any<HeatmapFilter>(), 3, Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow> { new() { Key = "A57 Warrington Road, Prescot", Count = 14 }, new() { Key = "M57 J2", Count = 2 } });
        var handler = new GetAreaStatsQueryHandler(_heatmap, Access(SubscriptionPlan.Premium), _options);

        var stats = await handler.Handle(new GetAreaStatsQuery(new HeatmapRequest(Prescot, from, new DateOnly(2026, 10, 18))), Ct);

        Assert.False(stats.Suppressed);
        Assert.Equal(38, stats.Total);
        Assert.Equal(12, stats.ChangePercent);
        Assert.Equal(8, stats.BusiestHour);
        Assert.Equal(new[] { IncidentCategory.DoubleYellows, IncidentCategory.Tailgating, IncidentCategory.ClosePassCyclist }, stats.TopCategories.Select(c => c.Category));
        Assert.Equal(34, stats.TopCategories[0].Percent);
        Assert.Equal("A57 Warrington Road, Prescot", Assert.Single(stats.TopPlaces).Name);
        Assert.Equal("A57 Warrington Road, Prescot", stats.PlaceName);
        Assert.Equal(new DateOnly(2026, 9, 13), stats.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 9, 30), stats.PreviousTo);
    }

    [Fact]
    public async Task Csv_export_is_for_premium_plus_only()
    {
        _heatmap.CountCellsAsync(Arg.Any<HeatmapFilter>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCellRow> { new() { CellY = 1186, CellX = -40, Count = 5 } });

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            new ExportHeatmapQueryHandler(_heatmap, Access(SubscriptionPlan.Premium), _options)
                .Handle(new ExportHeatmapQuery(new HeatmapRequest(Prescot), 9), Ct));

        var csv = await new ExportHeatmapQueryHandler(_heatmap, Access(SubscriptionPlan.PremiumPlus), _options)
            .Handle(new ExportHeatmapQuery(new HeatmapRequest(Prescot), 9), Ct);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.StartsWith("from,to,centre_latitude", lines[0]);
        Assert.EndsWith(",5", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Theory]
    [InlineData(18, 0.0045)]
    [InlineData(14, 0.0045)]
    [InlineData(13, 0.009)]
    [InlineData(11, 0.036)]
    [InlineData(10, 0.045)]
    [InlineData(6, 0.09)]
    public void Cell_size_follows_the_zoom(int zoom, double size) =>
        Assert.Equal((decimal)size, HeatmapGrid.LatitudeSize(zoom));

    [Fact]
    public void Rounded_pins_are_the_centres_of_the_finest_cells()
    {
        var (lat, _) = PinRounding.Round(53.4123m, -2.8m);
        var index = HeatmapGrid.CellIndex(lat, HeatmapGrid.FinestLatitudeSize);
        Assert.Equal(lat, decimal.Round(index * HeatmapGrid.FinestLatitudeSize, 6));
    }

    [Fact]
    public void Requests_are_parsed_strictly()
    {
        Assert.Equal(new GeoBox(53.4m, 53.45m, -2.85m, -2.75m), HeatmapRequest.ParseBox(Prescot));
        Assert.Null(HeatmapRequest.ParseBox("-2.75,53.4,-2.85,53.45"));
        Assert.Null(HeatmapRequest.ParseBox("1,2,3"));
        Assert.Equal(new[] { IncidentCategory.Tailgating, IncidentCategory.ClosePassCyclist }, HeatmapRequest.ParseCategories("tailgating, ClosePassCyclist"));
        Assert.Equal(IncidentTaxonomy.In(IncidentGroup.Parking), HeatmapRequest.ParseCategories("parking"));
        Assert.Null(HeatmapRequest.ParseCategories("Tailgating,Joyriding"));
        Assert.Null(HeatmapRequest.ParseCategories("3"));
        Assert.Equal(new HourRange(22, 6), HourRange.Parse("22-6"));
        Assert.True(new HourRange(22, 6).Contains(23) && new HourRange(22, 6).Contains(5) && !new HourRange(22, 6).Contains(12));
        Assert.Null(HourRange.Parse("7-7"));
        Assert.Null(HourRange.Parse("25-3"));
        Assert.Contains(new GetHeatmapQueryValidator().Validate(new GetHeatmapQuery(new HeatmapRequest("nope"), 6)).Errors, e => e.PropertyName == "bbox");
        Assert.True(new GetHeatmapQueryValidator().Validate(new GetHeatmapQuery(new HeatmapRequest(Prescot, Categories: "Tailgating", Hours: "7-10"), 12)).IsValid);
    }

    [Fact]
    public async Task Vehicle_filters_raise_the_threshold_to_5()
    {
        var (_, applied) = Access(SubscriptionPlan.Premium).Resolve(new HeatmapRequest(Prescot, VehicleTypes: "Van", Colours: "White"));
        _heatmap.CountCellsAsync(Arg.Any<HeatmapFilter>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCellRow> { new() { CellY = 11867, CellX = -396, Count = 4 } });

        var dto = await new GetHeatmapQueryHandler(_heatmap, Access(SubscriptionPlan.Premium), _options)
            .Handle(new GetHeatmapQuery(new HeatmapRequest(Prescot, VehicleTypes: "Van", Colours: "White"), 12), Ct);

        Assert.True(applied.HasVehicleFilters);
        Assert.Equal(5, dto.MinCellCount);
        Assert.Empty(dto.Cells);
        await _heatmap.Received().CountCellsAsync(
            Arg.Is<HeatmapFilter>(f => f.VehicleTypes!.Single() == VehicleType.Van && f.Colours!.Single() == VehicleColour.White),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Is(5),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_model_filter_only_applies_at_town_level_or_wider()
    {
        // Prescot's box is about 6.6 km wide: too close for a model.
        var (narrow, appliedNarrow) = Access(SubscriptionPlan.Premium).Resolve(new HeatmapRequest(Prescot, Make: "ford", Model: "transit"));
        Assert.True(narrow.ModelFilterIgnored);
        Assert.Null(appliedNarrow.Model);
        Assert.Equal("Ford", HeatmapAccess.Filter(appliedNarrow, HeatmapRequest.ParseBox(Prescot)!).Make);

        var region = "-3.2,53.2,-2.4,53.7";
        var (wide, appliedWide) = Access(SubscriptionPlan.Premium).Resolve(new HeatmapRequest(region, Make: "ford", Model: "transit"));
        Assert.False(wide.ModelFilterIgnored);
        Assert.Equal("Transit", HeatmapAccess.Filter(appliedWide, HeatmapRequest.ParseBox(region)!).Model);
    }

    [Fact]
    public void Vehicle_filter_values_must_come_from_the_lists()
    {
        var validator = new GetHeatmapQueryValidator();

        Assert.True(validator.Validate(new GetHeatmapQuery(new HeatmapRequest(Prescot, VehicleTypes: "Car,van", Make: "BMW", Model: "3 series", Light: HeatmapLight.Dark), 10)).IsValid);
        Assert.False(validator.Validate(new GetHeatmapQuery(new HeatmapRequest(Prescot, VehicleTypes: "Spaceship"), 10)).IsValid);
        Assert.False(validator.Validate(new GetHeatmapQuery(new HeatmapRequest(Prescot, Make: "AB12CDE"), 10)).IsValid);
        Assert.False(validator.Validate(new GetHeatmapQuery(new HeatmapRequest(Prescot, Make: "BMW", Model: "Transit"), 10)).IsValid);
    }
}
