namespace BJK.Modules.User.Domain;

/// <summary>
/// The age policy (decided 19 Oct 2026): people aged 13 and over can join, by their declared date of birth.
/// Under-18s get the protections for children; viewing sensitive posts needs the separate age check (18+).
/// Ages are worked out on the UTC calendar date. Someone born on 29 February turns a year older on 1 March in a
/// non-leap year, as in UK law.
/// </summary>
public static class AgeRules
{
    /// <summary>Minimum age to have an account.</summary>
    public const int MinimumAge = 13;

    /// <summary>Adults: 18 and over. Under this, the account is a minor's (the token's <c>minor</c> claim).</summary>
    public const int AdultAge = 18;

    /// <summary>Oldest date of birth accepted, as an age: anything older is surely a typing mistake.</summary>
    public const int OldestAge = 120;

    /// <summary>Validation error code for a date of birth under <see cref="MinimumAge"/>, so apps can show the refusal page.</summary>
    public const string TooYoungErrorCode = "too_young";

    /// <summary>The message for someone under 13. Kind and plain.</summary>
    public const string TooYoungMessage =
        "Sorry, you need to be 13 or over to join Bad Jockeys. You can still watch posts without an account.";

    /// <summary>Today's date for age purposes (UTC).</summary>
    /// <param name="clock">The clock.</param>
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>Age in whole years on <paramref name="today"/>.</summary>
    /// <param name="dateOfBirth">The date of birth.</param>
    /// <param name="today">The date to work out the age on.</param>
    public static int AgeOn(DateOnly dateOfBirth, DateOnly today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (today.Month < dateOfBirth.Month || (today.Month == dateOfBirth.Month && today.Day < dateOfBirth.Day))
        {
            age--;
        }

        return age;
    }

    /// <summary>True if someone with this date of birth may have an account (13 or over).</summary>
    /// <param name="dateOfBirth">The date of birth.</param>
    /// <param name="today">Today.</param>
    public static bool IsOldEnoughToJoin(DateOnly dateOfBirth, DateOnly today) => AgeOn(dateOfBirth, today) >= MinimumAge;

    /// <summary>True if someone with this date of birth is under 18.</summary>
    /// <param name="dateOfBirth">The date of birth.</param>
    /// <param name="today">Today.</param>
    public static bool IsMinor(DateOnly dateOfBirth, DateOnly today) => AgeOn(dateOfBirth, today) < AdultAge;

    /// <summary>True if the date is a believable date of birth: not in the future and not over 120 years ago.</summary>
    /// <param name="dateOfBirth">The date of birth.</param>
    /// <param name="today">Today.</param>
    public static bool IsPlausible(DateOnly dateOfBirth, DateOnly today) =>
        dateOfBirth <= today && AgeOn(dateOfBirth, today) <= OldestAge;
}
