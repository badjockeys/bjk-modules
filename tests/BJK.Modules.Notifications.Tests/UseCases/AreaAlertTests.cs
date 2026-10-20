using System.Net;
using System.Text;
using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Notifications.UseCases;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Notifications.Tests.TestData;

namespace BJK.Modules.Notifications.Tests.UseCases;

public sealed class AreaAlertTests
{
    // Rainhill, and a post about 1.5 km away. 2026-10-20 is in British Summer Time (UTC+1).
    private const decimal HomeLat = 53.42m;
    private const decimal HomeLng = -2.76m;
    private static readonly DateTimeOffset Afternoon = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero); // 14:00 UK
    private static readonly DateTimeOffset Night = new(2026, 10, 20, 22, 30, 0, TimeSpan.Zero); // 23:30 UK

    private readonly IAreaAlertRepository _areas = Substitute.For<IAreaAlertRepository>();
    private readonly INotificationRepository _notifications = Substitute.For<INotificationRepository>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly FixedTimeProvider _clock = new(Afternoon);
    private readonly List<Notification> _added = [];
    private readonly List<AreaAlertItem> _held = [];

    public AreaAlertTests()
    {
        _notifications.When(r => r.Add(Arg.Any<Notification>())).Do(c => _added.Add(c.Arg<Notification>()));
        _areas.When(r => r.AddItem(Arg.Any<AreaAlertItem>())).Do(c => _held.Add(c.Arg<AreaAlertItem>()));
        _blocks.GetBlockedEitherWayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationSender Sender() => new(
        _notifications,
        Substitute.For<IProfileDirectory>(),
        _email,
        Options.Create(new EmailOptions()),
        Options.Create(new NotificationsOptions { AppUrl = new Uri("https://badjockeys.com") }),
        _clock,
        NullLogger<NotificationSender>.Instance);

    private AreaAlerts Alerts() => new(_areas, _blocks, Sender(), _clock, NullLogger<AreaAlerts>.Instance);

    private AreaSubscription Subscriber(Guid userId, AreaAlertMode mode, int radiusKm = 5, bool premium = true)
    {
        var s = AreaSubscription.Create(userId, "Rainhill", HomeLat, HomeLng, Now);
        s.SetAlerts(true, radiusKm, mode, email: false, Now);
        s.RecordPlan(premium, _clock.GetUtcNow());
        return s;
    }

    private static PublishedPost Post(decimal lat = 53.43m, decimal lng = -2.77m, Guid? author = null) =>
        new(PostId, author ?? Guid.NewGuid(), "Tailgated on the A57", "A57, Rainhill", lat, lng, Afternoon);

    private void Around(params AreaSubscription[] subscriptions) =>
        _areas.FindAroundAsync(Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(subscriptions.ToList());

    [Fact]
    public void Home_areas_are_kept_to_about_a_kilometre()
    {
        var s = AreaSubscription.Create(UserId, "Rainhill", 53.41678m, -2.76612m, Now);

        Assert.Equal(53.42m, s.Latitude);
        Assert.Equal(-2.77m, s.Longitude);
        Assert.Equal(AreaSubscription.DefaultRadiusKm, s.RadiusKm);
        Assert.Equal(AreaAlertMode.Digest, s.Mode);
    }

    [Fact]
    public async Task Instant_subscribers_in_range_hear_straight_away_and_digest_ones_get_it_held()
    {
        var instant = Subscriber(UserId, AreaAlertMode.Instant);
        var digest = Subscriber(SamId, AreaAlertMode.Digest);
        Around(instant, digest);

        await Alerts().PostPublishedAsync(Post(), Ct);

        var sent = Assert.Single(_added);
        Assert.Equal(UserId, sent.UserId);
        Assert.Equal(NotificationType.AreaPost, sent.Type);
        Assert.Equal(SamId, Assert.Single(_held).UserId);
    }

    [Fact]
    public async Task Nobody_hears_outside_their_radius_or_without_premium_or_about_their_own_post()
    {
        Around(
            Subscriber(UserId, AreaAlertMode.Instant, radiusKm: 2),
            Subscriber(SamId, AreaAlertMode.Instant, premium: false),
            Subscriber(PostId, AreaAlertMode.Instant));

        // About 11 km away: outside 2 km. The third subscriber wrote the post.
        await Alerts().PostPublishedAsync(Post(lat: 53.52m, author: PostId), Ct);

        Assert.Empty(_added);
        Assert.Empty(_held);
    }

    [Fact]
    public async Task Quiet_hours_and_the_daily_cap_hold_instant_alerts_for_the_morning()
    {
        var s = Subscriber(UserId, AreaAlertMode.Instant);
        Around(s);
        for (var i = 0; i < AreaSubscription.InstantDailyCap + 2; i++)
        {
            await Alerts().PostPublishedAsync(Post(), Ct);
        }

        Assert.Equal(AreaSubscription.InstantDailyCap, _added.Count);
        Assert.Equal(2, _held.Count);

        _clock.Now = Night;
        Assert.True(UkTime.IsQuietHours(Night));
        await Alerts().PostPublishedAsync(Post(), Ct);
        Assert.Equal(3, _held.Count);
    }

    [Fact]
    public async Task Summaries_go_once_a_day_at_their_time_with_one_bell_notification()
    {
        var s = Subscriber(UserId, AreaAlertMode.Digest);
        s.SetAlerts(true, 5, AreaAlertMode.Digest, email: true, Now);
        _areas.ListWithWaitingAsync(Arg.Any<CancellationToken>()).Returns(new List<AreaSubscription> { s });
        var items = new List<AreaAlertItem>
        {
            AreaAlertItem.Create(UserId, Guid.NewGuid(), "Phone at the wheel", "Prescot", Night.AddDays(-1)),
            AreaAlertItem.Create(UserId, PostId, "Tailgated on the A57", "Rainhill", Night.AddDays(-1).AddMinutes(5)),
        };
        _areas.GetWaitingAsync(UserId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(items);
        _notifications.GetContactAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(NotificationContact.Create(UserId, "roadhog@example.com", Now));

        // 07:30 UK: not yet (Digest goes at 08:00).
        var morning = new DateTimeOffset(2026, 10, 21, 6, 30, 0, TimeSpan.Zero);
        await AreaSummaries.SendDueAsync(_areas, Sender(), morning, NullLogger.Instance, Ct);
        Assert.Empty(_added);

        // 08:05 UK: sent, once.
        var eight = new DateTimeOffset(2026, 10, 21, 7, 5, 0, TimeSpan.Zero);
        await AreaSummaries.SendDueAsync(_areas, Sender(), eight, NullLogger.Instance, Ct);
        var summary = Assert.Single(_added);
        Assert.Equal(2, summary.Count);
        Assert.Equal(PostId, summary.PostId);
        Assert.Equal("2 new posts in your area, including “Tailgated on the A57”.", NotificationTexts.Sentence(summary.Type, null, summary.Count, summary.PostTitle));
        _areas.Received(1).RemoveItems(items);
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.Subject == "2 new reports of bad driving near Rainhill" && m.TextBody.Contains("Phone at the wheel")),
            Arg.Any<CancellationToken>());

        await AreaSummaries.SendDueAsync(_areas, Sender(), eight.AddMinutes(10), NullLogger.Instance, Ct);
        Assert.Single(_added);
    }

    [Fact]
    public async Task Alerts_need_premium_and_a_home_area()
    {
        var caller = Substitute.For<ICurrentUser>();
        caller.IdentityId.Returns(UserId.ToString());
        var plans = Substitute.For<IPlanResolver>();
        plans.CurrentPlan().Returns(SubscriptionPlan.Free);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => new SetAreaAlertsCommandHandler(_areas, caller, _clock, plans).Handle(new SetAreaAlertsCommand(true), Ct));

        plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(
            () => new SetAreaAlertsCommandHandler(_areas, caller, _clock, plans).Handle(new SetAreaAlertsCommand(true), Ct));

        var home = await new SetHomeAreaCommandHandler(_areas, caller, _clock, plans).Handle(new SetHomeAreaCommand("Rainhill", 53.4168m, -2.7661m), Ct);
        Assert.Equal("Rainhill", home.PlaceName);
        Assert.True(home.CanUseAlerts);
        Assert.False(home.Enabled);
    }

    [Theory]
    [InlineData("Rainhill", "places?q=Rainhill&limit=10", """{"result":[{"name_1":"Rainhill","district_borough":"St. Helens","region":"North West","latitude":53.41678,"longitude":-2.76612}]}""", "Rainhill", "St. Helens, North West")]
    [InlineData("WA10 1AA", "postcodes/WA10%201AA", """{"result":{"outcode":"WA10","admin_district":"St. Helens","region":"North West","latitude":53.453,"longitude":-2.742}}""", "WA10", "St. Helens, North West")]
    [InlineData("wa10", "outcodes/wa10", """{"result":{"outcode":"WA10","admin_district":["St. Helens"],"latitude":53.451,"longitude":-2.761}}""", "WA10", "St. Helens")]
    public async Task Places_are_found_by_name_postcode_or_postcode_area_and_never_keep_a_full_postcode(
        string query,
        string expectedPath,
        string json,
        string name,
        string detail)
    {
        var handler = new StubHandler(json);
        var lookup = new PostcodesIoPlaceLookup(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.postcodes.io/") },
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PostcodesIoPlaceLookup>.Instance);

        var place = Assert.Single(await lookup.SearchAsync(query, Ct));

        Assert.Equal("https://api.postcodes.io/" + expectedPath, handler.LastUrl);
        Assert.Equal(name, place.Name);
        Assert.Equal(detail, place.Detail);
        Assert.Equal(2, BitConverter.GetBytes(decimal.GetBits(place.Latitude)[3])[2]);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
