using Microsoft.Extensions.Options;

namespace BJK.Modules.Safety;

/// <summary>Online safety settings shared by the modules. Section <c>Safety</c>.</summary>
public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    /// <summary>
    /// When true, posting, editing posts, commenting, reacting and reporting need a token with the
    /// <c>age_verified</c> claim (the age check, 18 and over). Default <b>false</b> since the age policy changed on
    /// 19 Oct 2026: people aged 13 and over can join, and the age check is only needed to view sensitive posts.
    /// Kept for future use.
    /// </summary>
    public bool RequireAgeVerification { get; set; }

    /// <summary>Limits for accounts in their first day. Section <c>Safety:NewAccounts</c>.</summary>
    public NewAccountLimitOptions NewAccounts { get; set; } = new();

    /// <summary>Keeping posts about driving: reports that hold a post, and pausing repeat posters. Section <c>Safety:OffTopic</c>.</summary>
    public OffTopicOptions OffTopic { get; set; } = new();
}

/// <summary>
/// Owner, 23 Oct 2026: posts must be about driving. A post reported as "Not a driving incident" or spam by
/// <see cref="ReportsToHold"/> different established members (or once by a Trusted Reporter) is held for a moderator
/// straight away. Someone who has <see cref="RemovalsToPause"/> posts removed as off-topic within
/// <see cref="Window"/> can't post again until <see cref="Pause"/> after the latest removal.
/// </summary>
public sealed class OffTopicOptions
{
    /// <summary>Default 3. 0 turns holding on reports off.</summary>
    public int ReportsToHold { get; set; } = 3;

    /// <summary>Default 2. 0 turns pausing off.</summary>
    public int RemovalsToPause { get; set; } = 2;

    /// <summary>Default 30 days.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Default 7 days.</summary>
    public TimeSpan Pause { get; set; } = TimeSpan.FromDays(7);
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
        if (limits.Period < TimeSpan.Zero || limits.MaxPosts < 0 || limits.MaxComments < 0)
        {
            return ValidateOptionsResult.Fail("Safety:NewAccounts:Period, MaxPosts and MaxComments can't be negative.");
        }

        var offTopic = options.OffTopic;
        return offTopic.ReportsToHold < 0 || offTopic.RemovalsToPause < 0 || offTopic.Window < TimeSpan.Zero || offTopic.Pause < TimeSpan.Zero
            ? ValidateOptionsResult.Fail("Safety:OffTopic:ReportsToHold, RemovalsToPause, Window and Pause can't be negative.")
            : ValidateOptionsResult.Success;
    }
}
