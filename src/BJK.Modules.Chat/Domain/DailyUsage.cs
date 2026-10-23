namespace BJK.Modules.Chat.Domain;

/// <summary>
/// How many questions a member has asked on one UK day. Only today's row is kept: older ones are deleted the next
/// time the member asks, and every row goes when the account is deleted.
/// </summary>
public sealed class DailyUsage
{
    private DailyUsage()
    {
    }

    public DailyUsage(Guid userId, DateOnly day, int questions)
    {
        UserId = userId;
        Day = day;
        Questions = questions;
    }

    public Guid UserId { get; private set; }

    /// <summary>The UK calendar day.</summary>
    public DateOnly Day { get; private set; }

    public int Questions { get; private set; }
}
