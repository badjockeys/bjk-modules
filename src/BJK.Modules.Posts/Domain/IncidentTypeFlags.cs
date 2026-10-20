namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A post's types of bad driving packed into one integer: bit n is set when the post has the
/// <see cref="IncidentCategory"/> whose value is n. One column holds every type a post has, so the feed, reels and heat
/// map filter with a single bitwise test and no join. <see cref="IncidentCategory"/> values must therefore never be
/// reordered or reused: new ones go on the end (there's room for 31).
/// </summary>
public static class IncidentTypeFlags
{
    /// <summary>The bit for one type.</summary>
    public static int Bit(IncidentCategory type) => 1 << (int)type;

    /// <summary>The flags for a set of types (duplicates ignored).</summary>
    public static int From(IEnumerable<IncidentCategory> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var flags = 0;
        foreach (var type in types)
        {
            flags |= Bit(type);
        }

        return flags;
    }

    /// <summary>
    /// The types in <paramref name="flags"/>, the main type first and the rest in their usual order. Flags of 0 (a row
    /// written before types existed) mean just the main type.
    /// </summary>
    public static IReadOnlyList<IncidentCategory> ToList(int flags, IncidentCategory main)
    {
        var types = new List<IncidentCategory> { main };
        foreach (var type in Enum.GetValues<IncidentCategory>())
        {
            if (type != main && (flags & Bit(type)) != 0)
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>
    /// The types to store for what the author chose: <paramref name="types"/> without duplicates, in their order, or
    /// just <paramref name="fallback"/> when none were given (older clients send one category).
    /// </summary>
    public static IReadOnlyList<IncidentCategory> Normalise(IReadOnlyList<IncidentCategory>? types, IncidentCategory? fallback)
    {
        if (types is { Count: > 0 })
        {
            return types.Distinct().ToList();
        }

        return fallback is { } single ? [single] : [];
    }

    /// <summary>
    /// Types where other people can be in the reporter's vehicle: everything except Bad parking (used for passenger
    /// tags).
    /// </summary>
    public static bool IsOnTheRoad(IncidentCategory type) => type != IncidentCategory.BadParking;
}
