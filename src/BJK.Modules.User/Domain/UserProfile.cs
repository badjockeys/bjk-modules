namespace BJK.Modules.User.Domain;

/// <summary>
/// A Bad Jockeys user profile. Credentials live in the identity server;
/// this record is linked to the identity account by <see cref="IdentityId"/> (the token's "sub" claim).
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

    public static UserProfile Create(string identityId, string email, string displayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new UserProfile(Guid.CreateVersion7(now), identityId, email.Trim(), displayName.Trim(), now);
    }

    public bool IsOwnedBy(string identityId) =>
        string.Equals(IdentityId, identityId, StringComparison.Ordinal);

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
