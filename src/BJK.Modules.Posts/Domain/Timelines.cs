using BJK.Modules.Shared.Security;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A member's custom timeline (<c>posts.Timelines</c>), shown as a tab next to For you and Following: posts with any of
/// its types of bad driving, optionally only within a radius of a place. Named, put in order and deleted by its owner.
/// How many each member can have depends on their plan (<see cref="LimitFor"/>).
/// </summary>
public sealed class Timeline
{
    public const int NameMaxLength = 30;
    public const int PlaceNameMaxLength = 100;

    /// <summary>The radiuses offered, in km.</summary>
    public static readonly IReadOnlyList<int> Radiuses = [2, 5, 10, 25, 50];

    // For EF Core.
    private Timeline()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The subcategories, as <see cref="IncidentTypeFlags"/> bits 0 to 63: posts with any of them.</summary>
    public long TypeFlags { get; private set; }

    /// <summary>Bits 64 to 127 of the subcategories.</summary>
    public long TypeFlagsHigh { get; private set; }

    /// <summary>All of its subcategories as one mask.</summary>
    public TypeMask Mask => new(TypeFlags, TypeFlagsHigh);

    /// <summary>The place's name, as chosen (shown only to the owner).</summary>
    public string? PlaceName { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public int? RadiusKm { get; private set; }

    /// <summary>Where the tab goes, from 0.</summary>
    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The types, in the order of the enum.</summary>
    public IReadOnlyList<IncidentCategory> Types =>
        IncidentTypeFlags.ToList(Mask);

    /// <summary>How many custom timelines a plan allows: Free 1, Premium 5, Premium + 10.</summary>
    public static int LimitFor(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.PremiumPlus => 10,
        SubscriptionPlan.Premium => 5,
        _ => 1,
    };

    public static Timeline Create(Guid userId, TimelineDetails details, int position, DateTimeOffset now)
    {
        var timeline = new Timeline
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Position = position,
            CreatedAt = now,
        };
        timeline.Update(details, now);
        return timeline;
    }

    public void Update(TimelineDetails details, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details.Name);
        if (details.Types.Count == 0)
        {
            throw new ArgumentException("Choose at least one type of bad driving.", nameof(details));
        }

        Name = details.Name.Trim();
        (TypeFlags, TypeFlagsHigh) = IncidentTypeFlags.From(details.Types);
        if (details.Area is { } area)
        {
            PlaceName = area.PlaceName.Trim();
            Latitude = Math.Round(area.Latitude, 2, MidpointRounding.AwayFromZero);
            Longitude = Math.Round(area.Longitude, 2, MidpointRounding.AwayFromZero);
            RadiusKm = area.RadiusKm;
        }
        else
        {
            (PlaceName, Latitude, Longitude, RadiusKm) = (null, null, null, null);
        }

        UpdatedAt = now;
    }

    public void MoveTo(int position) => Position = position;
}

/// <summary>A timeline's place: its name, about 1 km of precision, and the radius in km.</summary>
public sealed record TimelineArea(string PlaceName, decimal Latitude, decimal Longitude, int RadiusKm);

/// <summary>What a custom timeline shows.</summary>
public sealed record TimelineDetails(string Name, IReadOnlyCollection<IncidentCategory> Types, TimelineArea? Area = null);
