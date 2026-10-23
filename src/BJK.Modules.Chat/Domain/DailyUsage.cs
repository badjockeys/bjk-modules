namespace BJK.Modules.Chat.Domain;

/// <summary>
/// How many questions a member has asked on one UK day, and how many were blocked as misuse (strikes). Only today's row is kept: older ones are deleted the next
/// time the member asks, and every row goes when the account is deleted.
/// </summary>
public sealed class DailyUsage
{
    private DailyUsage()
    {
    }

    public DailyUsage(Guid userId, DateOnly day, int questions, int strikes = 0)
    {
        UserId = userId;
        Day = day;
        Questions = questions;
        Strikes = strikes;
    }

    public Guid UserId { get; private set; }

    /// <summary>The UK calendar day.</summary>
    public DateOnly Day { get; private set; }

    public int Questions { get; private set; }

    /// <summary>
    /// Questions blocked by the guard rails (off topic, about people or plates, prompt tricks, abuse). They don't use the
    /// allowance; at <c>Chat:MisuseLimit</c> the chat pauses until midnight.
    /// </summary>
    public int Strikes { get; private set; }
}
