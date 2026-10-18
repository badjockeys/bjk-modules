using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Safety;

/// <summary>Online safety settings shared by the modules. Section <c>Safety</c>.</summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>
    /// When true (the default), posting, editing posts, commenting, reacting and reporting need a token with the
    /// <c>age_verified</c> claim, which the identity server issues once the account has passed the age check.
    /// Set false only for local development and tests.
    /// </summary>
    public bool RequireAgeVerification { get; set; } = true;

    /// <summary>Limits for accounts in their first day. Section <c>Safety:NewAccounts</c>.</summary>
    public NewAccountLimitOptions NewAccounts { get; set; } = new();
}

/// <summary>
/// Rate limits for new accounts: while a profile is younger than <see cref="Period"/>, it can create at most
/// <see cref="MaxPosts"/> posts and <see cref="MaxComments"/> comments in any rolling <see cref="Period"/>.
/// </summary>
public sealed class NewAccountLimitOptions
{
    /// <summary>How long an account counts as new, and the rolling window the limits apply to. Default 24 hours.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Default 3.</summary>
    public int MaxPosts { get; set; } = 3;

    /// <summary>Default 20.</summary>
    public int MaxComments { get; set; } = 20;
}

internal sealed class SafetyOptionsValidator : IValidateOptions<SafetyOptions>
{
    public ValidateOptionsResult Validate(string? name, SafetyOptions options)
    {
        var limits = options.NewAccounts;
        return limits.Period < TimeSpan.Zero || limits.MaxPosts < 0 || limits.MaxComments < 0
            ? ValidateOptionsResult.Fail("Safety:NewAccounts:Period, MaxPosts and MaxComments can't be negative.")
            : ValidateOptionsResult.Success;
    }
}
