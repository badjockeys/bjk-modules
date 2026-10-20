using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;

namespace BJK.Modules.Profiles.UseCases;

/// <summary>
/// A profile. <c>RoadUser</c> (Driver, Cyclist or Both) is shown to others only if the user chose to show it;
/// <c>ShowRoadUser</c> is only filled in for the user themselves (null for others). <c>FollowerCount</c> and
/// <c>FollowingCount</c> count active profiles; <c>IsFollowing</c> (you follow them) and <c>FollowsYou</c> are null
/// when signed out or for yourself.
/// </summary>
public sealed record ProfileDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string? Bio,
    string? Location,
    Uri? AvatarUrl,
    DateTimeOffset CreatedAt,
    RoadUser? RoadUser = null,
    bool? ShowRoadUser = null,
    int FollowerCount = 0,
    int FollowingCount = 0,
    bool? IsFollowing = null,
    bool? FollowsYou = null)
{
    internal static ProfileDto From(Profile profile, Uri? avatarUrl, bool forOwner = true) => new(
        profile.Id,
        profile.Handle,
        profile.DisplayName,
        profile.Bio,
        profile.Location,
        avatarUrl,
        profile.CreatedAt,
        forOwner ? profile.RoadUser : profile.PublicRoadUser,
        forOwner ? profile.ShowRoadUser : null);
}

/// <summary>Is a handle free to take?</summary>
/// <param name="Handle">The handle as it would be stored.</param>
/// <param name="Available">True if the caller can have it.</param>
/// <param name="Reason">Why not, when it isn't available.</param>
public sealed record HandleAvailability(string Handle, bool Available, string? Reason);

internal static class Avatars
{
    /// <summary>Avatar image URLs for the given profiles, keyed by media id. Avatars that aren't Ready are left out.</summary>
    public static async Task<IReadOnlyDictionary<Guid, Uri>> GetUrlsAsync(
        IMediaDirectory media,
        IReadOnlyCollection<Profile> profiles,
        CancellationToken cancellationToken)
    {
        var ids = profiles.Select(p => p.AvatarMediaId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, Uri>();
        }

        var items = await media.GetAsync(ids, cancellationToken);
        return items
            .Where(m => m.PlaybackUrl is not null)
            .ToDictionary(m => m.Id, m => m.PlaybackUrl!);
    }

    public static Uri? UrlFor(Profile profile, IReadOnlyDictionary<Guid, Uri> urls) =>
        profile.AvatarMediaId is { } id && urls.TryGetValue(id, out var url) ? url : null;

    public static async Task<ProfileDto> ToDtoAsync(
        IMediaDirectory media,
        Profile profile,
        CancellationToken cancellationToken,
        bool forOwner = true)
    {
        var urls = await GetUrlsAsync(media, [profile], cancellationToken);
        return ProfileDto.From(profile, UrlFor(profile, urls), forOwner);
    }

    /// <summary>The profile with its follow counts, and whether the viewer and they follow each other.</summary>
    public static async Task<ProfileDto> WithFollowsAsync(
        IProfileRepository profiles,
        ProfileDto dto,
        Guid? viewerId,
        CancellationToken cancellationToken)
    {
        var (followers, following) = await profiles.CountFollowsAsync(dto.Id, cancellationToken);
        dto = dto with { FollowerCount = followers, FollowingCount = following };
        if (viewerId is not { } viewer || viewer == dto.Id)
        {
            return dto;
        }

        return dto with
        {
            IsFollowing = await profiles.GetFollowAsync(viewer, dto.Id, cancellationToken) is not null,
            FollowsYou = await profiles.GetFollowAsync(dto.Id, viewer, cancellationToken) is not null,
        };
    }
}
