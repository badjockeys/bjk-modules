namespace BJK.Modules.Notifications.Domain;

/// <summary>How area alerts arrive.</summary>
public enum AreaAlertMode
{
    /// <summary>One summary a day, at 08:00 UK time (the default).</summary>
    Digest,

    /// <summary>
    /// Straight away, up to <see cref="AreaSubscription.InstantDailyCap"/> a day and never in quiet hours
    /// (22:00 to 07:00 UK time); anything held back comes as a summary when quiet hours end.
    /// </summary>
    Instant,
}

/// <summary>
/// A user's home area and their area alert settings, stored in <c>notifications.AreaSubscriptions</c>. The home area
/// is chosen with a place lookup and kept to about 1 km (2 decimal places); only its name is ever shown to others
/// (as the profile's town or area). Alerts are a Premium feature: they're only sent while
/// <see cref="IsPremium"/> was recorded recently (see <see cref="PlanValidFor"/>).
/// </summary>
public sealed class AreaSubscription
{
    public const int PlaceNameMaxLength = 100;

    /// <summary>The radiuses offered, in km.</summary>
    public static readonly IReadOnlyList<int> Radiuses = [2, 5, 10, 25];

    public const int DefaultRadiusKm = 5;

    /// <summary>The most instant alerts a day; further posts go into the next summary.</summary>
    public const int InstantDailyCap = 10;

    /// <summary>
    /// How long a recorded Premium plan counts. It's recorded each time the user checks their notifications, so a
    /// lapsed plan stops alerts within this time even if they never come back.
    /// </summary>
    public static readonly TimeSpan PlanValidFor = TimeSpan.FromDays(35);

    // For EF Core.
    private AreaSubscription()
    {
    }

    public Guid UserId { get; private set; }

    public string PlaceName { get; private set; } = string.Empty;

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    public int RadiusKm { get; private set; }

    public AreaAlertMode Mode { get; private set; }

    /// <summary>Alerts are switched on (needs Premium).</summary>
    public bool Enabled { get; private set; }

    /// <summary>Also email the alerts (summaries, and instant ones).</summary>
    public bool Email { get; private set; }

    /// <summary>The user's plan was Premium (or Premium +) when last recorded.</summary>
    public bool IsPremium { get; private set; }

    public DateTimeOffset? PlanRecordedAt { get; private set; }

    /// <summary>The UK day <see cref="SentToday"/> counts.</summary>
    public DateOnly? SentDay { get; private set; }

    /// <summary>Instant alerts sent on <see cref="SentDay"/>.</summary>
    public int SentToday { get; private set; }

    /// <summary>When the last summary went (so each is sent once).</summary>
    public DateTimeOffset? LastSummaryAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AreaSubscription Create(Guid userId, string placeName, decimal latitude, decimal longitude, DateTimeOffset now)
    {
        var subscription = new AreaSubscription
        {
            UserId = userId == Guid.Empty ? throw new ArgumentException("A user id is required.", nameof(userId)) : userId,
            RadiusKm = DefaultRadiusKm,
            Mode = AreaAlertMode.Digest,
        };
        subscription.SetHome(placeName, latitude, longitude, now);
        return subscription;
    }

    /// <summary>Moves the home area, rounded to about 1 km.</summary>
    public void SetHome(string placeName, decimal latitude, decimal longitude, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeName);
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Not a place on Earth.");
        }

        var name = placeName.Trim();
        PlaceName = name.Length <= PlaceNameMaxLength ? name : name[..PlaceNameMaxLength];
        Latitude = Math.Round(latitude, 2, MidpointRounding.AwayFromZero);
        Longitude = Math.Round(longitude, 2, MidpointRounding.AwayFromZero);
        UpdatedAt = now;
    }

    public void SetAlerts(bool enabled, int radiusKm, AreaAlertMode mode, bool email, DateTimeOffset now)
    {
        if (!Radiuses.Contains(radiusKm))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusKm), "Choose 2, 5, 10 or 25 km.");
        }

        Enabled = enabled;
        RadiusKm = radiusKm;
        Mode = mode;
        Email = email;
        UpdatedAt = now;
    }

    /// <summary>Records the user's plan (from their token). Returns true if anything changed.</summary>
    public bool RecordPlan(bool isPremium, DateTimeOffset now)
    {
        var stale = PlanRecordedAt is null || now - PlanRecordedAt > TimeSpan.FromDays(1);
        if (IsPremium == isPremium && !stale)
        {
            return false;
        }

        IsPremium = isPremium;
        PlanRecordedAt = now;
        return true;
    }

    /// <summary>Alerts go out: switched on, and Premium recently recorded.</summary>
    public bool Active(DateTimeOffset now) =>
        Enabled && IsPremium && PlanRecordedAt is { } at && now - at <= PlanValidFor;

    /// <summary>Distance in km from the home area to a point (great circle).</summary>
    public double DistanceKm(decimal latitude, decimal longitude)
    {
        const double EarthRadiusKm = 6371;
        double Rad(decimal degrees) => (double)degrees * Math.PI / 180;
        var dLat = Rad(latitude - Latitude);
        var dLng = Rad(longitude - Longitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + (Math.Cos(Rad(Latitude)) * Math.Cos(Rad(latitude)) * Math.Pow(Math.Sin(dLng / 2), 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// Takes one of today's instant alerts if there's room (and it isn't quiet hours). False: hold it for the summary.
    /// </summary>
    public bool TryTakeInstant(DateTimeOffset now)
    {
        if (Mode != AreaAlertMode.Instant || UkTime.IsQuietHours(now))
        {
            return false;
        }

        var today = UkTime.Today(now);
        if (SentDay != today)
        {
            SentDay = today;
            SentToday = 0;
        }

        if (SentToday >= InstantDailyCap)
        {
            return false;
        }

        SentToday++;
        return true;
    }

    /// <summary>The UK time of day summaries go: 08:00 for Digest, 07:00 (the end of quiet hours) for Instant.</summary>
    public TimeSpan SummaryTime => Mode == AreaAlertMode.Digest ? new TimeSpan(8, 0, 0) : new TimeSpan(7, 0, 0);

    /// <summary>
    /// Today's summary is due (it's past <see cref="SummaryTime"/> and none has gone today). It covers posts held
    /// before <see cref="SummaryCutoff"/>: anything held after that (an Instant subscriber over the daily cap) waits
    /// for tomorrow's.
    /// </summary>
    public bool SummaryDue(DateTimeOffset now) =>
        UkTime.Local(now).TimeOfDay >= SummaryTime
        && (LastSummaryAt is null || UkTime.Today(LastSummaryAt.Value) < UkTime.Today(now));

    /// <summary>Today's summary time, as an instant.</summary>
    public DateTimeOffset SummaryCutoff(DateTimeOffset now) => UkTime.At(UkTime.Today(now), SummaryTime);

    public void MarkSummarySent(DateTimeOffset now) => LastSummaryAt = now;
}

/// <summary>A post waiting for someone's next area summary, in <c>notifications.AreaAlertItems</c>.</summary>
public sealed class AreaAlertItem
{
    public const int TitleMaxLength = 120;

    // For EF Core.
    private AreaAlertItem()
    {
    }

    public Guid UserId { get; private set; }

    public Guid PostId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? PlaceName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AreaAlertItem Create(Guid userId, Guid postId, string title, string? placeName, DateTimeOffset now) => new()
    {
        UserId = userId,
        PostId = postId,
        Title = title.Length <= TitleMaxLength ? title : title[..TitleMaxLength],
        PlaceName = placeName is { Length: > 200 } ? placeName[..200] : placeName,
        CreatedAt = now,
    };
}

/// <summary>UK time for alerts: quiet hours (22:00 to 07:00) and the digest time.</summary>
public static class UkTime
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException("The Europe/London time zone isn't available (install tzdata in the container).");
    });

    public static DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime;

    public static DateOnly Today(DateTimeOffset instant) => DateOnly.FromDateTime(Local(instant));

    /// <summary>A UK date and time of day, as an instant.</summary>
    public static DateTimeOffset At(DateOnly day, TimeSpan time)
    {
        var local = day.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone.Value), TimeSpan.Zero);
    }

    /// <summary>22:00 to 07:00 UK time.</summary>
    public static bool IsQuietHours(DateTimeOffset instant)
    {
        var hour = Local(instant).Hour;
        return hour >= 22 || hour < 7;
    }
}
