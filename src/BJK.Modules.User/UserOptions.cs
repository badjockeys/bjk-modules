namespace BJK.Modules.User;

/// <summary>Section <c>Users</c>.</summary>
public sealed class UserOptions
{
    public const string SectionName = "Users";

    /// <summary>How long a deactivated account can be reactivated before it and all its data are deleted. Default 90 days.</summary>
    public TimeSpan DeactivationGracePeriod { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How often the identity server looks for accounts whose grace period has ended. Default hourly.</summary>
    public TimeSpan RetentionCheckInterval { get; set; } = TimeSpan.FromHours(1);
}
