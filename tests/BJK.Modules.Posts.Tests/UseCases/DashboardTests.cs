using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Dashboard;
using BJK.Modules.Posts.UseCases.Heatmap;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class DashboardTests
{
    private readonly IDashboardRepository _dashboard = Substitute.For<IDashboardRepository>();
    private readonly IHeatmapRepository _heatmap = Substitute.For<IHeatmapRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public DashboardTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DashboardWidgetDetails Area(DashboardKind kind = DashboardKind.AreaIncidents, params IncidentCategory[] types) =>
        new(kind, "L34 5", 53.4291m, -2.8015m, 2, 30, types);

    private static DashboardItem Widget(int position) => DashboardItem.Widget(UserId, Area(), position, Now);

    private void Has(params DashboardItem[] rows) =>
        _dashboard.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(rows.ToList());

    private DashboardFigures Figures() => new(_heatmap, Options.Create(new HeatmapOptions()), _clock);

    [Theory]
    [InlineData(SubscriptionPlan.Free, 0)]
    [InlineData(SubscriptionPlan.Premium, 3)]
    [InlineData(SubscriptionPlan.PremiumPlus, 5)]
    public void Free_gets_no_widgets_Premium_3_and_Premium_plus_5(SubscriptionPlan plan, int limit) =>
        Assert.Equal(limit, DashboardItem.LimitFor(plan));

    [Fact]
    public async Task With_nothing_saved_everyone_gets_the_built_in_cards_in_order()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);
        Has();

        var dto = await new GetDashboardQueryHandler(_dashboard, _plans, _caller).Handle(new GetDashboardQuery(), Ct);

        Assert.Equal(new[] { "Trending", "WhoToFollow", "CategoryTrends" }, dto.Items.Select(i => i.Key));
        Assert.All(dto.Items, i => Assert.True(i.BuiltIn));
        Assert.Equal(0, dto.Limit);
        Assert.Equal(0, dto.Used);
    }

    [Fact]
    public async Task Free_members_can_not_add_widgets()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);
        Has();
        var handler = new CreateDashboardWidgetCommandHandler(_dashboard, _plans, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(
            new CreateDashboardWidgetCommand(DashboardKind.AreaIncidents, "L34 5", 53.4291m, -2.8015m, 2, 30),
            Ct));
        _dashboard.DidNotReceive().Add(Arg.Any<DashboardItem>());
    }

    [Fact]
    public async Task A_new_widget_goes_at_the_end_after_the_built_in_cards_are_saved()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        Has();
        var handler = new CreateDashboardWidgetCommandHandler(_dashboard, _plans, _caller, _clock);

        var dto = await handler.Handle(
            new CreateDashboardWidgetCommand(DashboardKind.TypeTrend, " Prescot ", 53.4291m, -2.8015m, 5, 90, [IncidentCategory.PhoneInHand]),
            Ct);

        Assert.Equal(new[] { "Trending", "WhoToFollow", "CategoryTrends" }, dto.Items.Take(3).Select(i => i.Key));
        var widget = dto.Items[3];
        Assert.Equal(DashboardKind.TypeTrend, widget.Kind);
        Assert.Equal("Prescot", widget.PlaceName);
        Assert.Equal(53.43m, widget.Latitude);
        Assert.Equal(new[] { IncidentCategory.PhoneInHand }, widget.Types);
        Assert.Equal(1, dto.Used);
        _dashboard.Received(4).Add(Arg.Any<DashboardItem>());
    }

    [Fact]
    public async Task Premium_stops_at_3_widgets()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        Has(Widget(0), Widget(1), Widget(2));
        var handler = new CreateDashboardWidgetCommandHandler(_dashboard, _plans, _caller, _clock);

        var error = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(
            new CreateDashboardWidgetCommand(DashboardKind.TopRoads, "Leeds", 53.8m, -1.55m, 10, 7),
            Ct));

        Assert.Contains("Your plan allows 3 widgets", error.Message);
    }

    [Fact]
    public async Task After_a_downgrade_widgets_past_the_limit_are_locked_and_give_no_figures()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        var widgets = new[] { Widget(0), Widget(1), Widget(2), Widget(3) };
        Has(widgets);

        var dto = await new GetDashboardQueryHandler(_dashboard, _plans, _caller).Handle(new GetDashboardQuery(), Ct);
        Assert.Equal(new[] { false, false, false, true }, dto.Items.Where(i => !i.BuiltIn).Select(i => i.Locked));

        var data = new GetDashboardWidgetDataQueryHandler(_dashboard, Figures(), _plans, _caller);
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Handle(new GetDashboardWidgetDataQuery(widgets[3].Id), Ct));
    }

    [Fact]
    public async Task Reordering_lists_every_card_once_built_in_ones_by_name()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        var widget = Widget(3);
        Has(
            DashboardItem.BuiltIn(UserId, DashboardKind.CategoryTrends, 0, Now),
            DashboardItem.BuiltIn(UserId, DashboardKind.Trending, 1, Now),
            DashboardItem.BuiltIn(UserId, DashboardKind.WhoToFollow, 2, Now),
            widget);
        var handler = new ReorderDashboardCommandHandler(_dashboard, _plans, _caller, _clock);

        var dto = await handler.Handle(new ReorderDashboardCommand([widget.Id.ToString(), "whotofollow", "CategoryTrends", "Trending"]), Ct);
        Assert.Equal(new[] { widget.Id.ToString(), "WhoToFollow", "CategoryTrends", "Trending" }, dto.Items.Select(i => i.Key));

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ReorderDashboardCommand(["Trending", "CategoryTrends"]), Ct));
    }

    [Fact]
    public async Task Built_in_cards_can_be_hidden_on_any_plan()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);
        Has();

        var dto = await new SetDashboardCardHiddenCommandHandler(_dashboard, _plans, _caller, _clock)
            .Handle(new SetDashboardCardHiddenCommand(DashboardKind.WhoToFollow, true), Ct);

        Assert.True(dto.Items.Single(i => i.Kind == DashboardKind.WhoToFollow).Hidden);
        Assert.False(new SetDashboardCardHiddenCommandValidator().Validate(new SetDashboardCardHiddenCommand(DashboardKind.TopRoads, true)).IsValid);
    }

    [Fact]
    public void Widgets_need_a_place_a_radius_and_period_from_the_lists_and_up_to_3_types()
    {
        var validator = new CreateDashboardWidgetCommandValidator();
        Assert.True(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.AreaIncidents, "L34", 53.4m, -2.8m, 2, 30)).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.AreaIncidents, "", 53.4m, -2.8m, 2, 30)).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.AreaIncidents, "L34", 53.4m, -2.8m, 3, 30)).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.AreaIncidents, "L34", 53.4m, -2.8m, 2, 14)).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.Trending, "L34", 53.4m, -2.8m, 2, 30)).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(
            DashboardKind.AreaIncidents, "L34", 53.4m, -2.8m, 2, 30,
            [IncidentCategory.Tailgating, IncidentCategory.PhoneInHand, IncidentCategory.SpeedingInTown, IncidentCategory.ClosePassCyclist])).IsValid);
        Assert.False(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.TypeTrend, "L34", 53.4m, -2.8m, 2, 30)).IsValid);
        Assert.True(validator.Validate(new CreateDashboardWidgetCommand(DashboardKind.TypeTrend, "L34", 53.4m, -2.8m, 2, 30, [IncidentCategory.Tailgating])).IsValid);
    }

    [Fact]
    public async Task Under_3_incidents_a_widget_shows_nothing()
    {
        _heatmap.CountAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>()).Returns(2);

        var data = await Figures().CountAsync(Area(), Ct);

        Assert.True(data.Suppressed);
        Assert.Null(data.Total);
        Assert.Empty(data.Counts);
        await _heatmap.DidNotReceive().CountByPeriodAsync(Arg.Any<HeatmapFilter>(), Arg.Any<TrendPeriod>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Incidents_in_an_area_give_the_total_change_a_daily_line_and_top_types()
    {
        // The last 30 UK days to 18 October, then the 30 before.
        _heatmap.CountAsync(Arg.Is<HeatmapFilter>(f => f.From == UkCalendar.StartOf(new DateOnly(2026, 9, 19))), Arg.Any<CancellationToken>()).Returns(12);
        _heatmap.CountAsync(Arg.Is<HeatmapFilter>(f => f.From == UkCalendar.StartOf(new DateOnly(2026, 8, 20))), Arg.Any<CancellationToken>()).Returns(10);
        _heatmap.CountByPeriodAsync(Arg.Any<HeatmapFilter>(), TrendPeriod.Day, Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapTrendRow> { new HeatmapTrendRow { Period = new DateTime(2026, 10, 18), Count = 5 } });
        _heatmap.CountByCategoryAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow> { new HeatmapCountRow { Key = "Tailgating", Count = 6 }, new HeatmapCountRow { Key = "PhoneInHand", Count = 2 } });

        var data = await Figures().CountAsync(Area(), Ct);

        Assert.False(data.Suppressed);
        Assert.Equal(12, data.Total);
        Assert.Equal(20, data.ChangePercent);
        Assert.Equal(30, data.Counts.Count);
        Assert.Equal(5, data.Counts[^1]);
        Assert.Equal(new DateOnly(2026, 10, 18), data.PeriodStarts[^1]);
        // Types under 3 aren't shown.
        Assert.Equal(new[] { IncidentCategory.Tailgating }, data.TopCategories.Select(c => c.Category));
    }

    [Fact]
    public async Task When_it_happens_hides_bands_and_days_under_3()
    {
        _heatmap.CountAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>()).Returns(9);
        _heatmap.CountByHourAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow> { new HeatmapCountRow { Key = "8", Count = 4 }, new HeatmapCountRow { Key = "9", Count = 3 }, new HeatmapCountRow { Key = "17", Count = 2 } });
        _heatmap.CountByWeekdayAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow> { new HeatmapCountRow { Key = "0", Count = 7 }, new HeatmapCountRow { Key = "6", Count = 2 } });

        var data = await Figures().CountAsync(Area(DashboardKind.WhenItHappens), Ct);

        Assert.Equal(7, data.TimesOfDay.Single(b => b.FromHour == 8).Count);
        Assert.Null(data.TimesOfDay.Single(b => b.FromHour == 16).Count);
        Assert.Equal(8, data.BusiestHour);
        Assert.Equal(DayOfWeek.Monday, data.Weekdays[0].Day);
        Assert.Equal(7, data.Weekdays[0].Count);
        Assert.Equal(DayOfWeek.Sunday, data.Weekdays[6].Day);
        Assert.Null(data.Weekdays[6].Count);
    }
}
