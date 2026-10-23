using BJK.Modules.Posts.Domain;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Filters shared by the feed and reels queries.</summary>
internal static class PostFilters
{
    /// <summary>Posts with any of the subcategories in <paramref name="mask"/> (an empty mask matches every post).</summary>
    public static IQueryable<Post> WithAnyType(this IQueryable<Post> query, TypeMask mask)
    {
        var (low, high) = (mask.Low, mask.High);
        return (low, high) switch
        {
            (0, 0) => query,
            (_, 0) => query.Where(p => (p.TypeFlags & low) != 0),
            (0, _) => query.Where(p => (p.TypeFlagsHigh & high) != 0),
            _ => query.Where(p => (p.TypeFlags & low) != 0 || (p.TypeFlagsHigh & high) != 0),
        };
    }

    /// <summary>Posts in this group or with this subcategory, whichever are given.</summary>
    public static IQueryable<Post> InCategory(this IQueryable<Post> query, IncidentGroup? group, IncidentCategory? category)
    {
        if (group is { } g)
        {
            // Any of the post's subcategories, not just the main one, can put it in the group.
            query = query.WithAnyType(IncidentTypeFlags.From([g]));
        }

        return category is { } c ? query.WithAnyType(IncidentTypeFlags.Bit(c)) : query;
    }
}
