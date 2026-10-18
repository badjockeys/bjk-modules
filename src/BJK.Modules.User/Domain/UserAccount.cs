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

    /// <summary>Age check (minimum age 18). Only the outcome is kept: never the video, the photo, the estimate or ID details.</summary>
    public AgeCheckStatus AgeCheckStatus { get; private set; } = AgeCheckStatus.NotChecked;

    /// <summary>How the current <see cref="AgeCheckStatus"/> was reached, if any attempt was made.</summary>
    public AgeCheckMethod? AgeCheckMethod { get; private set; }

    /// <summary>The outcome of the most recent attempt.</summary>
    public AgeCheckResult? AgeCheckResult { get; private set; }

    /// <summary>Which provider gave the most recent result, e.g. "Development" or the estimator's name.</summary>
    public string? AgeCheckProvider { get; private set; }

    /// <summary>When the most recent result was recorded.</summary>
    public DateTimeOffset? AgeCheckRecordedAt { get; private set; }

    /// <summary>Attempts made since <see cref="AgeCheckAttemptsSince"/>, for the daily limit.</summary>
    public int AgeCheckAttempts { get; private set; }

    /// <summary>Start of the current 24-hour attempts window.</summary>
    public DateTimeOffset? AgeCheckAttemptsSince { get; private set; }

    public bool IsActive => Status == UserAccountStatus.Active;

    /// <summary>True once the account has passed the age check; tokens then carry age_verified=true.</summary>
    public bool IsAgeVerified => AgeCheckStatus == AgeCheckStatus.Passed;

    /// <summary>
    /// The head-turn selfie is only for a first check: once the estimate says 18 to 24 (or under 18), photo ID is the
    /// only way forward, so repeating the selfie can't be used to fish for a higher estimate.
    /// </summary>
    public bool CanUseFaceCheck => AgeCheckStatus == AgeCheckStatus.NotChecked;

    /// <summary>Photo ID: any time before passing, except after photo ID has already shown the user is under 18.</summary>
    public bool CanUseDocumentCheck => AgeCheckStatus switch
    {
        AgeCheckStatus.NotChecked or AgeCheckStatus.DocumentRequired => true,
        AgeCheckStatus.Failed => AgeCheckMethod == Domain.AgeCheckMethod.FaceEstimate,
        _ => false,
    };

    /// <summary>How many more attempts (face or photo ID) the user can make in the current 24 hours.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="maxPerDay">The daily limit.</param>
    public int AgeCheckAttemptsLeft(DateTimeOffset now, int maxPerDay) =>
        AgeCheckAttemptsSince is { } since && now - since < TimeSpan.FromDays(1)
            ? Math.Max(0, maxPerDay - AgeCheckAttempts)
            : maxPerDay;

    /// <summary>Counts an attempt against the daily limit. Returns false (and counts nothing) if none are left.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="maxPerDay">The daily limit.</param>
    public bool TryStartAgeCheckAttempt(DateTimeOffset now, int maxPerDay)
    {
        if (AgeCheckAttemptsSince is not { } since || now - since >= TimeSpan.FromDays(1))
        {
            AgeCheckAttemptsSince = now;
            AgeCheckAttempts = 0;
        }

        if (AgeCheckAttempts >= maxPerDay)
        {
            return false;
        }

        AgeCheckAttempts++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Records an attempt's outcome. A pass, a "needs photo ID" or an under-18 result moves the status; an
    /// inconclusive attempt (liveness failed, uncertain estimate, unreadable ID) leaves it where it was.
    /// </summary>
    /// <param name="method">Face estimate or photo ID.</param>
    /// <param name="result">The outcome.</param>
    /// <param name="provider">Which provider decided it.</param>
    /// <param name="now">The current time.</param>
    public void RecordAgeCheck(AgeCheckMethod method, AgeCheckResult result, string provider, DateTimeOffset now)
    {
        if (IsAgeVerified)
        {
            throw new InvalidOperationException("This account has already passed the age check.");
        }

        AgeCheckStatus? decided = result switch
        {
            Domain.AgeCheckResult.Passed => AgeCheckStatus.Passed,
            Domain.AgeCheckResult.Under25 => AgeCheckStatus.DocumentRequired,
            Domain.AgeCheckResult.Underage => AgeCheckStatus.Failed,
            _ => null,
        };

        if (decided is { } status)
        {
            AgeCheckStatus = status;
            AgeCheckMethod = method;
        }

        AgeCheckResult = result;
        AgeCheckProvider = provider.Length > ProviderMaxLength ? provider[..ProviderMaxLength] : provider;
        AgeCheckRecordedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Admins: clears the age check so the user takes it again, with a fresh set of attempts.</summary>
    /// <param name="now">The current time.</param>
    public void ResetAgeCheck(DateTimeOffset now)
    {
        AgeCheckStatus = AgeCheckStatus.NotChecked;
        AgeCheckMethod = null;
        AgeCheckResult = null;
        AgeCheckProvider = null;
        AgeCheckRecordedAt = null;
        AgeCheckAttempts = 0;
        AgeCheckAttemptsSince = null;
        UpdatedAt = now;
    }

    /// <summary>Longest provider name stored.</summary>
    public const int ProviderMaxLength = 100;

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
