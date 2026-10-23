namespace BJK.Modules.Posts.Domain;

/// <summary>A set of subcategories as bits across two numbers: bit n of <see cref="Low"/> (n &lt; 64) or of
/// <see cref="High"/> (n - 64) is set when the set has the <see cref="IncidentCategory"/> whose value is n.</summary>
public readonly record struct TypeMask(long Low, long High)
{
    public bool IsEmpty => Low == 0 && High == 0;

    public bool Has(IncidentCategory type) => (this & IncidentTypeFlags.Bit(type)) is { IsEmpty: false };

    public bool Overlaps(TypeMask other) => (Low & other.Low) != 0 || (High & other.High) != 0;

    public static TypeMask operator |(TypeMask a, TypeMask b) => new(a.Low | b.Low, a.High | b.High);

    public static TypeMask operator &(TypeMask a, TypeMask b) => new(a.Low & b.Low, a.High & b.High);
}

/// <summary>
/// A post's subcategories packed into two integers (<c>TypeFlags</c> and <c>TypeFlagsHigh</c>, see
/// <see cref="TypeMask"/>): one place holds every subcategory a post has, so the feed, reels and heat map filter with a
/// bitwise test and no join. <see cref="IncidentCategory"/> values must therefore never be reordered or reused: new ones
/// go on the end (there's room for 128).
/// </summary>
public static class IncidentTypeFlags
{
    /// <summary>The bit for one subcategory.</summary>
    public static TypeMask Bit(IncidentCategory type)
    {
        var n = (int)type;
        return n < 64 ? new TypeMask(1L << n, 0) : new TypeMask(0, 1L << (n - 64));
    }

    /// <summary>The flags for a set of subcategories (duplicates ignored).</summary>
    public static TypeMask From(IEnumerable<IncidentCategory> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var mask = default(TypeMask);
        foreach (var type in types)
        {
            mask |= Bit(type);
        }

        return mask;
    }

    /// <summary>Every subcategory in these groups.</summary>
    public static TypeMask From(IEnumerable<IncidentGroup> groups) => From(groups.SelectMany(IncidentTaxonomy.In));

    /// <summary>The subcategories in a mask, in the order they're listed.</summary>
    public static IReadOnlyList<IncidentCategory> ToList(TypeMask mask) =>
        Enum.GetValues<IncidentCategory>().Where(mask.Has).ToList();

    /// <summary>
    /// The subcategories in a mask, the main one first and the rest in their usual order. An empty mask means just the
    /// main one.
    /// </summary>
    public static IReadOnlyList<IncidentCategory> ToList(TypeMask mask, IncidentCategory main) =>
        [main, .. ToList(mask).Where(t => t != main)];

    /// <summary>
    /// The subcategories to store for what the author chose: <paramref name="types"/> without duplicates, in their order,
    /// or just <paramref name="fallback"/> when none were given (clients that send one category).
    /// </summary>
    public static IReadOnlyList<IncidentCategory> Normalise(IReadOnlyList<IncidentCategory>? types, IncidentCategory? fallback)
    {
        if (types is { Count: > 0 })
        {
            return types.Distinct().ToList();
        }

        return fallback is { } single ? [single] : [];
    }

    /// <summary>Subcategories where other people can be in the reporter's vehicle (see <see cref="IncidentTaxonomy.IsOnTheRoad"/>).</summary>
    public static bool IsOnTheRoad(IncidentCategory type) => IncidentTaxonomy.IsOnTheRoad(type);
}
