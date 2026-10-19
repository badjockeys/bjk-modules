namespace BJK.Modules.User.Tests;

/// <summary>Dates of birth for the tests (the tests' "now" is in October 2026).</summary>
internal static class TestDates
{
    /// <summary>An adult's date of birth.</summary>
    public static readonly DateOnly Adult = new(1990, 6, 15);

    /// <summary>A 15-year-old's date of birth.</summary>
    public static readonly DateOnly Fifteen = new(2011, 3, 1);
}
