using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Heatmap;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class CategoryTrendsTests
{
    private const string Prescot = "-2.85,53.40,-2.75,53.45";

    private readonly IHeatmapRepository _heatmap = Substitute.For<IHeatmapRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly IOptions<HeatmapOptions> _options = Options.Create(new HeatmapOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetCategoryTrendsQueryHandler Handler(SubscriptionPlan? plan)
    {
        _plans.CurrentPlan().Returns(plan);
        return new GetCategoryTrendsQueryHandler(_heatmap, new HeatmapAccess(_plans, _options, new FixedTimeProvider(Now)), _options);
    }

    private static HeatmapTrendRow Row(IncidentCategory category, int year, int month, int day, int count) =>
        new() { Key = category.ToString(), Period = new DateTime(year, month, day), Count = count };

    [Fact]
    public async Task Free_and_signed_out_get_the_last_7_days_for_the_whole_UK_whatever_they_ask()
    {
        var trends = await Handler(null).Handle(new GetCategoryTrendsQuery(Prescot, new DateOnly(2025, 1, 1), null), Ct);

        Assert.Equal(new DateOnly(2026, 10, 12), trends.Access.From);
        Assert.Equal(new DateOnly(2026, 10, 18), trends.Access.To);
        Assert.Equal(TrendPeriod.Day, trends.Period);
        Assert.Equal(7, trends.Periods.Count);
        Assert.False(trends.AreaAllowed);
        Assert.True(trends.AreaIgnored);
        Assert.Equal(new DateOnly(2026, 10, 5), trends.PreviousFrom);
        Assert.Equal(new DateOnly(2026, 10, 11), trends.PreviousTo);
        await _heatmap.Received().CountByCategoryAndPeriodAsync(
            Arg.Is<HeatmapFilter>(f => f.Area.MinLatitude == -90m && f.Area.MaxLongitude == 180m && f.Categories.Count == 0),
            TrendPeriod.Day,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Premium_can_narrow_to_an_area_and_long_ranges_are_weekly_or_monthly()
    {
        var premium = await Handler(SubscriptionPlan.Premium).Handle(new GetCategoryTrendsQuery(Prescot, new DateOnly(2026, 1, 1), null), Ct);

        Assert.True(premium.AreaAllowed);
        Assert.False(premium.AreaIgnored);
        Assert.Equal(TrendPeriod.Week, premium.Period);
        Assert.Equal(new DateOnly(2025, 12, 29), premium.Periods[0]);
        Assert.All(premium.Periods, p => Assert.Equal(DayOfWeek.Monday, p.DayOfWeek));
        await _heatmap.Received().CountByCategoryAndPeriodAsync(
            Arg.Is<HeatmapFilter>(f => f.Area.MinLatitude == 53.40m && f.Area.MaxLongitude == -2.75m),
            TrendPeriod.Week,
            Arg.Any<CancellationToken>());

        var plus = await Handler(SubscriptionPlan.PremiumPlus).Handle(new GetCategoryTrendsQuery(null, new DateOnly(2022, 3, 15), null), Ct);
        Assert.Equal(TrendPeriod.Month, plus.Period);
        Assert.Equal(new DateOnly(2022, 3, 1), plus.Periods[0]);
        Assert.Equal(new DateOnly(2026, 10, 1), plus.Periods[^1]);
        Assert.False(plus.AreaIgnored);
    }

    [Fact]
    public async Task Each_type_gets_a_line_a_total_and_a_change_and_small_ones_are_suppressed()
    {
        _heatmap.CountByCategoryAndPeriodAsync(Arg.Any<HeatmapFilter>(), Arg.Any<TrendPeriod>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapTrendRow>
            {
                Row(IncidentCategory.Tailgating, 2026, 10, 12, 4),
                Row(IncidentCategory.Tailgating, 2026, 10, 18, 8),
                Row(IncidentCategory.BadParking, 2026, 10, 14, 5),
                Row(IncidentCategory.Speeding, 2026, 10, 13, 2),
            });
        _heatmap.CountByCategoryAsync(Arg.Any<HeatmapFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<HeatmapCountRow>
            {
                new() { Key = "Tailgating", Count = 8 },
                new() { Key = "BadParking", Count = 2 },
            });

        var trends = await Handler(SubscriptionPlan.Free).Handle(new GetCategoryTrendsQuery(null, null, null), Ct);

        Assert.Equal(Enum.GetValues<IncidentCategory>().Length, trends.Categories.Count);
        var tailgating = trends.Categories[0];
        Assert.Equal(IncidentCategory.Tailgating, tailgating.Category);
        Assert.Equal(12, tailgating.Total);
        Assert.Equal(new[] { 4, 0, 0, 0, 0, 0, 8 }, tailgating.Points);
        Assert.Equal(8, tailgating.PreviousTotal);
        Assert.Equal(50, tailgating.ChangePercent);

        var parking = trends.Categories[1];
        Assert.Equal(IncidentCategory.BadParking, parking.Category);
        Assert.Equal(5, parking.Total);
        Assert.Null(parking.PreviousTotal);
        Assert.Null(parking.ChangePercent);

        var speeding = Assert.Single(trends.Categories, c => c.Category == IncidentCategory.Speeding);
        Assert.True(speeding.Suppressed);
        Assert.Null(speeding.Total);
        Assert.Empty(speeding.Points);
        Assert.All(trends.Categories.Skip(2), c => Assert.True(c.Suppressed));
    }

    [Theory]
    [InlineData(7, TrendPeriod.Day)]
    [InlineData(92, TrendPeriod.Day)]
    [InlineData(93, TrendPeriod.Week)]
    [InlineData(731, TrendPeriod.Week)]
    [InlineData(732, TrendPeriod.Month)]
    public void The_period_follows_the_length_of_the_range(int days, TrendPeriod expected) =>
        Assert.Equal(expected, TrendPeriods.For(days));

    [Fact]
    public void Validation_rejects_a_bad_box_and_reversed_dates()
    {
        var validator = new GetCategoryTrendsQueryValidator();
        Assert.True(validator.Validate(new GetCategoryTrendsQuery(null, null, null)).IsValid);
        Assert.False(validator.Validate(new GetCategoryTrendsQuery("nonsense", null, null)).IsValid);
        Assert.False(validator.Validate(new GetCategoryTrendsQuery(null, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1))).IsValid);
    }
}
