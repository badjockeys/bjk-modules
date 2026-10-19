namespace BJK.Modules.User.Domain;

/// <summary>
/// The liveness check at sign-up: a short head-turn selfie video showing one real person, to stop bots and fake
/// accounts. It doesn't estimate age. An account can't get tokens until it passes (see
/// <see cref="UserAccount.IsSignUpComplete"/>). Only this status and the date are kept, never the video.
/// </summary>
public enum LivenessStatus
{
    /// <summary>Not tried yet.</summary>
    NotChecked,

    /// <summary>The video showed one live person turning their head.</summary>
    Passed,

    /// <summary>The latest try didn't pass. The user can try again (3 tries a day).</summary>
    Failed,
}
