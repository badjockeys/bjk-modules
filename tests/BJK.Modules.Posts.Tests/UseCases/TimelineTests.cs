using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Timelines;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

public sealed class TimelineTests
{
    private readonly ITimelineRepository _timelines = Substitute.For<ITimelineRepository>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public TimelineTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Timeline Existing(int position) =>
        Timeline.Create(UserId, new TimelineDetails($"Mine {position}", [IncidentCategory.SpeedingInTown]), position, Now);

    [Theory]
    [InlineData(SubscriptionPlan.Free, 1)]
    [InlineData(SubscriptionPlan.Premium, 5)]
    [InlineData(SubscriptionPlan.PremiumPlus, 10)]
    public void Plans_allow_1_5_or_10_custom_timelines(SubscriptionPlan plan, int limit) =>
        Assert.Equal(limit, Timeline.LimitFor(plan));

    [Fact]
    public async Task A_new_timeline_goes_at_the_end_with_its_types_and_place()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        _timelines.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Timeline> { Existing(0), Existing(3) });
        var handler = new CreateTimelineCommandHandler(_timelines, _plans, _caller, _clock);

        var dto = await handler.Handle(
            new CreateTimelineCommand("School run", [IncidentCategory.DoubleYellows, IncidentCategory.SpeedingInTown, IncidentCategory.DoubleYellows], "Prescot", 53.4291m, -2.8015m, 5),
            Ct);

        Assert.Equal(4, dto.Position);
        Assert.Equal(new[] { IncidentCategory.DoubleYellows, IncidentCategory.SpeedingInTown }, dto.Types);
        Assert.Equal(53.43m, dto.Latitude);
        Assert.Equal(5, dto.RadiusKm);
        _timelines.Received(1).Add(Arg.Any<Timeline>());
    }

    [Fact]
    public async Task Free_members_get_one_custom_timeline()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Free);
        _timelines.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Timeline> { Existing(0) });
        var handler = new CreateTimelineCommandHandler(_timelines, _plans, _caller, _clock);

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new CreateTimelineCommand("Another", [IncidentCategory.Tailgating]), Ct));

        Assert.Contains("Your plan allows 1 custom timeline", error.Message);
        _timelines.DidNotReceive().Add(Arg.Any<Timeline>());
    }

    [Fact]
    public void Timelines_need_a_name_a_type_and_a_whole_place_if_any()
    {
        var validator = new CreateTimelineCommandValidator();
        Assert.True(validator.Validate(new CreateTimelineCommand("M62", [IncidentCategory.Undertaking])).IsValid);
        Assert.False(validator.Validate(new CreateTimelineCommand("", [IncidentCategory.Undertaking])).IsValid);
        Assert.False(validator.Validate(new CreateTimelineCommand("M62", [])).IsValid);
        Assert.False(validator.Validate(new CreateTimelineCommand("M62", [IncidentCategory.Undertaking], "Leeds", 53.8m, null, 5)).IsValid);
        Assert.False(validator.Validate(new CreateTimelineCommand("M62", [IncidentCategory.Undertaking], "Leeds", 53.8m, -1.5m, 7)).IsValid);
        Assert.True(validator.Validate(new CreateTimelineCommand("M62", [IncidentCategory.Undertaking], "Leeds", 53.8m, -1.5m, 10)).IsValid);
        Assert.False(validator.Validate(new CreateTimelineCommand("Call 07700 900123", [IncidentCategory.Undertaking])).IsValid);
    }

    [Fact]
    public async Task Reordering_needs_every_timeline_once()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        var (a, b) = (Existing(0), Existing(1));
        _timelines.ListAsync(UserId, Arg.Any<CancellationToken>()).Returns(new List<Timeline> { a, b });
        var handler = new ReorderTimelinesCommandHandler(_timelines, _plans, _caller);

        var result = await handler.Handle(new ReorderTimelinesCommand([b.Id, a.Id]), Ct);

        Assert.Equal(new[] { b.Id, a.Id }, result.Timelines.Select(t => t.Id));
        Assert.Equal(0, b.Position);
        Assert.Equal(1, a.Position);
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ReorderTimelinesCommand([a.Id]), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ReorderTimelinesCommand([a.Id, a.Id]), Ct));
    }
}
