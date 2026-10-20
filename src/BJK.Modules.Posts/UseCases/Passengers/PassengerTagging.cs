using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;

namespace BJK.Modules.Posts.UseCases.Passengers;

/// <summary>
/// Checks and saves the passengers an author tags on a post, by @handle. Only on driving incidents (not bad parking),
/// up to <see cref="PostPassenger.MaxPerPost"/>, never the author, and only adults the author hasn't blocked (and
/// who haven't blocked them). Anyone who can't be tagged gets the same message, so it never says why (such as their
/// age). Each new tag waits for the person to accept before their name shows.
/// </summary>
public sealed class PassengerTagging(
    IPostRepository posts,
    IProfileDirectory profiles,
    IBlockDirectory blocks,
    TimeProvider clock)
{
    /// <summary>The field errors are reported against.</summary>
    public const string Field = "Passengers";

    /// <summary>Shown when passengers are tagged on a post that's only bad parking.</summary>
    public const string OnlyDrivingMessage = "Passengers can only be tagged on driving incidents, not bad parking.";

    /// <summary>
    /// The user ids for the handles, after every check. Empty if there are none. Throws a validation error naming the
    /// first handle that can't be tagged.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ResolveAsync(
        Guid authorId,
        IReadOnlyList<string>? handles,
        IReadOnlyList<IncidentCategory> types,
        CancellationToken cancellationToken)
    {
        var wanted = Normalise(handles);
        if (wanted.Count == 0)
        {
            return [];
        }

        if (!PostPassenger.AllowedFor(types))
        {
            throw PostAccess.Invalid(Field, OnlyDrivingMessage);
        }

        if (wanted.Count > PostPassenger.MaxPerPost)
        {
            throw PostAccess.Invalid(Field, $"You can tag up to {PostPassenger.MaxPerPost} passengers.");
        }

        var blocked = (await blocks.GetBlockedEitherWayAsync(authorId, cancellationToken)).ToHashSet();
        var ids = new List<Guid>();
        foreach (var handle in wanted)
        {
            var profile = await profiles.FindByHandleAsync(handle, cancellationToken)
                ?? throw PostAccess.Invalid(Field, $"We couldn't find @{handle}.");
            if (profile.UserId == authorId)
            {
                throw PostAccess.Invalid(Field, "You can't tag yourself.");
            }

            if (!profile.IsAdult || blocked.Contains(profile.UserId))
            {
                throw PostAccess.Invalid(Field, CantTag(handle));
            }

            ids.Add(profile.UserId);
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Makes the post's tags match <paramref name="userIds"/>: removes the others and tags new people (Pending).
    /// Someone who declined a tag on this post can't be tagged on it again. Returns the newly tagged user ids. Call
    /// before saving the post.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ApplyAsync(Post post, IReadOnlyList<Guid> userIds, CancellationToken cancellationToken)
    {
        var existing = await posts.GetPassengersAsync([post.Id], cancellationToken);
        var declined = existing.FirstOrDefault(p => p.Status == PassengerTagStatus.Declined && userIds.Contains(p.UserId));
        if (declined is not null)
        {
            var who = (await profiles.GetSummariesAsync([declined.UserId], cancellationToken))
                .TryGetValue(declined.UserId, out var p) ? p.Handle : "them";
            throw PostAccess.Invalid(Field, CantTag(who));
        }

        foreach (var tag in existing.Where(p => !userIds.Contains(p.UserId) && p.Status != PassengerTagStatus.Declined))
        {
            posts.RemovePassenger(tag);
        }

        var now = clock.GetUtcNow();
        var added = userIds.Where(id => existing.All(p => p.UserId != id)).ToList();
        foreach (var id in added)
        {
            posts.AddPassenger(PostPassenger.Tag(post.Id, id, now));
        }

        return added;
    }

    /// <summary>Removes every tag but declined ones (when an edit leaves only bad parking).</summary>
    public async Task ClearAsync(Post post, CancellationToken cancellationToken)
    {
        foreach (var tag in await posts.GetPassengersAsync([post.Id], cancellationToken))
        {
            if (tag.Status != PassengerTagStatus.Declined)
            {
                posts.RemovePassenger(tag);
            }
        }
    }

    private static string CantTag(string handle) => $"You can't tag @{handle}.";

    private static List<string> Normalise(IReadOnlyList<string>? handles) =>
        handles is null
            ? []
            : handles
                .Select(h => (h ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant())
                .Where(h => h.Length > 0)
                .Distinct()
                .ToList();
}
