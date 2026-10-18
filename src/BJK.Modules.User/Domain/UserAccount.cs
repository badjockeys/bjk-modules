namespace BJK.Modules.User.Domain;

public enum UserAccountStatus
{
    Active,

    /// <summary>
    /// The user (or an admin) deactivated the account. It can't sign in and its content is hidden, but nothing is
    /// deleted until <see cref="UserAccount.DeletionDueAt"/>. Until then the user can reactivate it.
    /// </summary>
    Deactivated,
}

/// <summary>
/// A Bad Jockeys account's status, in <c>users.Accounts</c>. Its <see cref="Id"/> is the sign-in account's id
/// (the token's "sub"), the same GUID used everywhere. Nothing else is copied here: the email and password live in
/// the sign-in account (<c>auth.AspNetUsers</c>), and the display name, handle and so on in the core profile.
/// </summary>
public sealed class UserAccount
{
    // For EF Core.
    private UserAccount()
    {
    }

    public Guid Id { get; private set; }

    public UserAccountStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    /// <summary>When a deactivated account and all its data are permanently deleted, unless it's reactivated first.</summary>
    public DateTimeOffset? DeletionDueAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool IsActive => Status == UserAccountStatus.Active;

    /// <summary>Creates the account record for a newly registered sign-in account.</summary>
    /// <param name="identityId">The sign-in account's id. Must be a GUID; it becomes this record's id.</param>
    /// <param name="now">The registration time.</param>
    public static UserAccount Create(string identityId, DateTimeOffset now)
    {
        if (!Guid.TryParse(identityId, out var id) || id == Guid.Empty)
        {
            throw new ArgumentException("The account id must be a GUID.", nameof(identityId));
        }

        return new UserAccount { Id = id, Status = UserAccountStatus.Active, CreatedAt = now };
    }

    public bool IsOwnedBy(Guid userId) => Id == userId;

    /// <summary>Starts the grace period. Deactivating an already deactivated account changes nothing.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="gracePeriod">How long the user has to reactivate.</param>
    /// <returns>True if the account was active.</returns>
    public bool Deactivate(DateTimeOffset now, TimeSpan gracePeriod)
    {
        if (Status == UserAccountStatus.Deactivated)
        {
            return false;
        }

        Status = UserAccountStatus.Deactivated;
        DeactivatedAt = now;
        DeletionDueAt = now + gracePeriod;
        UpdatedAt = now;
        return true;
    }

    /// <summary>Ends the grace period. Reactivating an active account changes nothing.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True if the account was deactivated.</returns>
    public bool Reactivate(DateTimeOffset now)
    {
        if (Status == UserAccountStatus.Active)
        {
            return false;
        }

        Status = UserAccountStatus.Active;
        DeactivatedAt = null;
        DeletionDueAt = null;
        UpdatedAt = now;
        return true;
    }

    public bool IsDueForDeletion(DateTimeOffset now) =>
        Status == UserAccountStatus.Deactivated && DeletionDueAt <= now;
}
