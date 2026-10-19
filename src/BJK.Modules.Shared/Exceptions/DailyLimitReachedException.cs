namespace BJK.Modules.Shared.Exceptions;

/// <summary>
/// The caller has used today's allowance (e.g. free reel plays). Returned as 403 with <c>code</c> = <c>daily_limit</c>,
/// <c>resetsAt</c> (when the next UK day starts) and <c>limit</c>, so the apps can show the upgrade sheet.
/// </summary>
/// <param name="message">What happened, in words the user understands.</param>
/// <param name="limit">Today's allowance.</param>
/// <param name="resetsAt">When the allowance starts again.</param>
public sealed class DailyLimitReachedException(string message, int limit, DateTimeOffset resetsAt) : Exception(message)
{
    /// <summary>The stable code the apps look for.</summary>
    public const string Code = "daily_limit";

    /// <summary>Today's allowance.</summary>
    public int Limit { get; } = limit;

    /// <summary>When the allowance starts again (midnight UK time).</summary>
    public DateTimeOffset ResetsAt { get; } = resetsAt;
}
