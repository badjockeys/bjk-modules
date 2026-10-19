namespace BJK.Modules.User.Domain;

/// <summary>
/// Where an account is in the age check: the "highly effective" check (18 and over) needed to view sensitive posts,
/// taken on demand. Only <see cref="Passed"/> earns the age_verified claim.
/// </summary>
public enum AgeCheckStatus
{
    /// <summary>No age check done yet, or an admin reset it.</summary>
    NotChecked,

    /// <summary>The face estimate was 18 to 24: the user must use photo ID to finish.</summary>
    DocumentRequired,

    /// <summary>The user is 18 or over.</summary>
    Passed,

    /// <summary>
    /// The user appears to be under 18. After a face estimate they may still use photo ID (estimates can be wrong);
    /// after photo ID it's final until an admin resets it.
    /// </summary>
    Failed,
}

/// <summary>How the age was checked.</summary>
public enum AgeCheckMethod
{
    /// <summary>Head-turn selfie video: liveness check plus facial age estimate.</summary>
    FaceEstimate,

    /// <summary>Photo of a government ID.</summary>
    Document,
}

/// <summary>The outcome of the most recent attempt. Never holds the estimated age or any detail from the ID.</summary>
public enum AgeCheckResult
{
    /// <summary>Estimated at or over the challenge age (25), or the ID shows 18 or over.</summary>
    Passed,

    /// <summary>Estimated 18 to 24: photo ID needed.</summary>
    Under25,

    /// <summary>The estimator couldn't give a confident age. The user can try again or use photo ID.</summary>
    Uncertain,

    /// <summary>Clearly under 18 (face estimate) or under 18 on the ID.</summary>
    Underage,

    /// <summary>The video didn't show one live person turning their head.</summary>
    LivenessFailed,

    /// <summary>The ID photo couldn't be read.</summary>
    DocumentUnreadable,
}

/// <summary>The age check's rules, kept free of any provider so they can be tested on their own.</summary>
public static class AgeCheckRules
{
    /// <summary>Age the check confirms: 18, for sensitive posts.</summary>
    public const int AdultAge = AgeRules.AdultAge;

    /// <summary>
    /// Decides a face estimate. <paramref name="estimatedAge"/> is null when the provider couldn't estimate.
    /// <paramref name="uncertainty"/> (years, either side) widens the margin: the lower bound must reach
    /// <paramref name="challengeAge"/> to pass, and the upper bound must be under 18 to fail outright.
    /// </summary>
    /// <param name="estimatedAge">The provider's estimate, or null.</param>
    /// <param name="uncertainty">The provider's margin of error in years (0 if it gives none).</param>
    /// <param name="challengeAge">Estimated age that passes without photo ID (25).</param>
    public static AgeCheckResult DecideFaceEstimate(double? estimatedAge, double uncertainty, int challengeAge)
    {
        if (estimatedAge is not { } age || double.IsNaN(age) || age <= 0)
        {
            return AgeCheckResult.Uncertain;
        }

        var margin = Math.Max(0, double.IsNaN(uncertainty) ? 0 : uncertainty);
        if (age - margin >= challengeAge)
        {
            return AgeCheckResult.Passed;
        }

        return age + margin < AdultAge ? AgeCheckResult.Underage : AgeCheckResult.Under25;
    }

    /// <summary>Decides a photo ID check. <paramref name="age"/> is null when the ID couldn't be read.</summary>
    /// <param name="age">Age from the date of birth on the ID, or null.</param>
    public static AgeCheckResult DecideDocument(int? age) => age switch
    {
        null => AgeCheckResult.DocumentUnreadable,
        >= AdultAge => AgeCheckResult.Passed,
        _ => AgeCheckResult.Underage,
    };
}
