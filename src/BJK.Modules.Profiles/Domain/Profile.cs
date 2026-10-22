namespace BJK.Modules.Profiles.Domain;

/// <summary>
/// A user's public profile, stored in <c>profiles.Profiles</c>. Its <see cref="Id"/> is the user's id
/// (the token's "sub"), the same GUID as their sign-in account and their User module profile.
/// The sign-in details (email, password) stay in the identity server.
/// </summary>
public sealed class Profile
{
    public const int DisplayNameMaxLength = 50;
    public const int BioMaxLength = 500;
    public const int LocationMaxLength = 100;

    // For EF Core.
    private Profile()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, lower-case, without the @. See <see cref="Domain.Handle"/>.</summary>
    public string Handle { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public string? Bio { get; private set; }

    /// <summary>Free text the user chooses, such as a town or region. Not a precise location.</summary>
    public string? Location { get; private set; }

    /// <summary>A Ready image from the Media module.</summary>
    public Guid? AvatarMediaId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>
    /// Set while the user's account is deactivated. The profile is hidden from everyone but kept (handle included)
    /// in case they come back; it's deleted for good when the account is.
    /// </summary>
    public DateTimeOffset? DeactivatedAt { get; private set; }

    public bool IsActive => DeactivatedAt is null;

    /// <summary>
    /// Under 18 by the date of birth they gave the identity server, as their own token last said (its "minor"
    /// claim). Recorded whenever they create, read or change their profile, so it lapses soon after their 18th
    /// birthday. Null until then: treated as under 18 for anything that needs an adult (such as being tagged).
    /// </summary>
    public bool? IsMinor { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Profile Create(
        Guid userId,
        string handle,
        string displayName,
        string? bio,
        string? location,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        var profile = new Profile
        {
            Id = userId,
            Handle = CheckedHandle(handle),
            CreatedAt = now,
        };
        profile.SetDetails(displayName, bio, location);
        return profile;
    }

    public bool IsOwnedBy(Guid userId) => Id == userId;

    /// <summary>Records whether they're under 18, from their token. Returns true if it changed.</summary>
    public bool RecordAgeBand(bool isMinor)
    {
        if (IsMinor == isMinor)
        {
            return false;
        }

        IsMinor = isMinor;
        return true;
    }

    public void Update(string displayName, string? bio, string? location, DateTimeOffset now)
    {
        SetDetails(displayName, bio, location);
        UpdatedAt = now;
    }

    public void ChangeHandle(string handle, DateTimeOffset now)
    {
        Handle = CheckedHandle(handle);
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        DeactivatedAt ??= now;
    }

    public void Reactivate()
    {
        DeactivatedAt = null;
    }

    /// <summary>
    /// A moderator's reset of everything the user wrote on their profile, after a report: the display name becomes
    /// the handle, and the bio, location and avatar are removed. Returns the removed avatar's media id, if any.
    /// </summary>
    public Guid? ResetPublicDetails(DateTimeOffset now)
    {
        DisplayName = Handle;
        Bio = null;
        Location = null;
        var avatar = AvatarMediaId;
        AvatarMediaId = null;
        UpdatedAt = now;
        return avatar;
    }

    /// <summary>Sets or (with null) removes the avatar. Returns the previous avatar's media id, if it changed.</summary>
    public Guid? SetAvatar(Guid? mediaId, DateTimeOffset now)
    {
        var previous = AvatarMediaId;
        if (previous == mediaId)
        {
            return null;
        }

        AvatarMediaId = mediaId;
        UpdatedAt = now;
        return previous;
    }

    private void SetDetails(string displayName, string? bio, string? location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = Limit(displayName.Trim(), DisplayNameMaxLength, nameof(displayName));
        Bio = Optional(bio, BioMaxLength, nameof(bio));
        Location = Optional(location, LocationMaxLength, nameof(location));
    }

    private static string CheckedHandle(string handle)
    {
        var normalised = Domain.Handle.Normalise(handle);
        var problem = Domain.Handle.Problem(normalised);
        return problem is null ? normalised : throw new ArgumentException(problem, nameof(handle));
    }

    private static string? Optional(string? value, int maxLength, string name) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), maxLength, name);

    private static string Limit(string value, int maxLength, string name) =>
        value.Length <= maxLength ? value : throw new ArgumentException($"Must be {maxLength} characters or fewer.", name);
}
