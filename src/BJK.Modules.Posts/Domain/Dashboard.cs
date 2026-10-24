using BJK.Modules.Shared.Security;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// What a dashboard card shows. The first three are the built-in cards everyone gets (they can be moved or hidden, not
/// deleted); the rest are widgets a Premium member sets up. Stored by name: add new ones at the end.
/// </summary>
public enum DashboardKind
{
    /// <summary>Category trends (built in).</summary>
    CategoryTrends,

    /// <summary>What's trending: hashtags and towns (built in).</summary>
    Trending,

    /// <summary>Who to follow (built in).</summary>
    WhoToFollow,

    /// <summary>Incidents in an area: total, change, a line over time and the top types.</summary>
    AreaIncidents,

    /// <summary>One type of bad driving in an area: total, change and a line over time.</summary>
    TypeTrend,

    /// <summary>When it happens in an area: times of day and days of the week.</summary>
    WhenItHappens,

    /// <summary>The most reported roads (place names) in an area.</summary>
    TopRoads,
}

/// <summary>
/// One card on a member's dashboard (<c>posts.DashboardItems</c>): the right-hand panel on desktop and the Dashboard tab
/// on their profile on phones (owner, 24 Oct 2026). A member with no rows has the default layout (the built-in cards,
/// in order). Built-in cards have only a position and whether they're hidden; widgets also have a place, a radius, a
/// period and optionally types. How many widgets a member can have depends on their plan (<see cref="LimitFor"/>).
/// </summary>
public sealed class DashboardItem
{
    public const int PlaceNameMaxLength = 100;
    public const int MaxTypes = 3;

    /// <summary>The built-in cards, in their default order.</summary>
    public static readonly IReadOnlyList<DashboardKind> BuiltIns = [DashboardKind.CategoryTrends, DashboardKind.Trending, DashboardKind.WhoToFollow];

    /// <summary>The radiuses offered, in km.</summary>
    public static readonly IReadOnlyList<int> Radiuses = [1, 2, 5, 10, 25];

    /// <summary>The periods offered, in days.</summary>
    public static readonly IReadOnlyList<int> Periods = [7, 30, 90, 365];

    // For EF Core.
    private DashboardItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DashboardKind Kind { get; private set; }

    /// <summary>Hidden by the member (built-in cards only).</summary>
    public bool Hidden { get; private set; }

    /// <summary>Where it goes, from 0.</summary>
    public int Position { get; private set; }

    /// <summary>The place's name, as chosen (shown only to the owner).</summary>
    public string? PlaceName { get; private set; }

    /// <summary>The place, to about 1 km.</summary>
    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public int? RadiusKm { get; private set; }

    public int? PeriodDays { get; private set; }

    /// <summary>The types, as <see cref="IncidentTypeFlags"/> bits 0 to 63 (none: every type).</summary>
    public long TypeFlags { get; private set; }

    /// <summary>Bits 64 to 127 of the types.</summary>
    public long TypeFlagsHigh { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsBuiltIn => IsBuiltInKind(Kind);

    /// <summary>The types, in the order of the enum (empty: every type).</summary>
    public IReadOnlyList<IncidentCategory> Types => IncidentTypeFlags.ToList(new TypeMask(TypeFlags, TypeFlagsHigh));

    public static bool IsBuiltInKind(DashboardKind kind) => BuiltIns.Contains(kind);

    /// <summary>How many widgets a plan allows: Free none (they see a greyed-out card), Premium 3, Premium + 5.</summary>
    public static int LimitFor(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.PremiumPlus => 5,
        SubscriptionPlan.Premium => 3,
        _ => 0,
    };

    /// <summary>A built-in card, placed (when a member first changes their layout).</summary>
    public static DashboardItem BuiltIn(Guid userId, DashboardKind kind, int position, DateTimeOffset now)
    {
        if (!IsBuiltInKind(kind))
        {
            throw new ArgumentException("Not a built-in card.", nameof(kind));
        }

        return new DashboardItem
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Kind = kind,
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public static DashboardItem Widget(Guid userId, DashboardWidgetDetails details, int position, DateTimeOffset now)
    {
        var item = new DashboardItem
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Kind = details.Kind,
            Position = position,
            CreatedAt = now,
        };
        item.Update(details, now);
        return item;
    }

    public void Update(DashboardWidgetDetails details, DateTimeOffset now)
    {
        if (IsBuiltInKind(details.Kind))
        {
            throw new ArgumentException("Built-in cards can't be changed into widgets.", nameof(details));
        }

        if (details.Kind == DashboardKind.TypeTrend && details.Types.Count != 1)
        {
            throw new ArgumentException("Choose one type of bad driving.", nameof(details));
        }

        Kind = details.Kind;
        PlaceName = details.PlaceName.Trim();
        Latitude = Math.Round(details.Latitude, 2, MidpointRounding.AwayFromZero);
        Longitude = Math.Round(details.Longitude, 2, MidpointRounding.AwayFromZero);
        RadiusKm = details.RadiusKm;
        PeriodDays = details.PeriodDays;
        (TypeFlags, TypeFlagsHigh) = IncidentTypeFlags.From(details.Types);
        UpdatedAt = now;
    }

    public void MoveTo(int position) => Position = position;

    /// <summary>Hides or shows a built-in card.</summary>
    public void SetHidden(bool hidden, DateTimeOffset now)
    {
        if (!IsBuiltIn)
        {
            throw new InvalidOperationException("Only built-in cards are hidden; widgets are deleted.");
        }

        Hidden = hidden;
        UpdatedAt = now;
    }

    /// <summary>The widget's settings, to count its figures with.</summary>
    public DashboardWidgetDetails? Details() => IsBuiltIn
        ? null
        : new DashboardWidgetDetails(Kind, PlaceName!, Latitude!.Value, Longitude!.Value, RadiusKm!.Value, PeriodDays!.Value, Types);
}

/// <summary>A widget's settings: what it shows, the place (name and point), the radius in km, the period in days and any types.</summary>
public sealed record DashboardWidgetDetails(
    DashboardKind Kind,
    string PlaceName,
    decimal Latitude,
    decimal Longitude,
    int RadiusKm,
    int PeriodDays,
    IReadOnlyList<IncidentCategory> Types);
