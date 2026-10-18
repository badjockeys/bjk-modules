namespace BJK.Modules.User.Domain;

/// <summary>
/// A Bad Jockeys user profile. Credentials live in the identity server.
/// The profile's <see cref="Id"/> is the same GUID as the sign-in account (the token's "sub" claim),
/// so every module identifies a person by one id. <see cref="IdentityId"/> holds the same value as text.
/// </summary>
public sealed class UserProfile
{
    public const int DisplayNameMaxLength = 50;
    public const int EmailMaxLength = 256;
    public const int IdentityIdMaxLength = 128;

    // For EF Core.
    private UserProfile()
    {
    }

    private UserProfile(Guid id, string identityId, string email, string displayName, DateTimeOffset createdAt)
    {
        Id = id;
        IdentityId = identityId;
        Email = email;
        DisplayName = displayName;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string IdentityId { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <param name="identityId">The sign-in account's id. Must be a GUID; it becomes the profile's id too.</param>
    public static UserProfile Create(string identityId, string email, string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (!Guid.TryParse(identityId, out var id) || id == Guid.Empty)
        {
            throw new ArgumentException("The account id must be a GUID.", nameof(identityId));
        }

        return new UserProfile(id, id.ToString("D"), email.Trim(), displayName.Trim(), now);
    }

    public bool IsOwnedBy(string identityId) =>
        Guid.TryParse(identityId, out var id) && id == Id;

    public void UpdateProfile(string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        EnsureNotDeleted();

        DisplayName = displayName.Trim();
        UpdatedAt = now;
    }

    /// <summary>Soft delete: the profile is hidden but kept, so posted footage keeps its uploader reference.</summary>
    public void Delete(DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        DeletedAt = now;
        UpdatedAt = now;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("A deleted user can't be changed.");
        }
    }
}
