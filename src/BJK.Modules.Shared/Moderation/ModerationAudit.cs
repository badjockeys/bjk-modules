using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Shared.Moderation;

/// <summary>
/// One moderation action, for the audit log. Who did it (actor id and role) and when are added by the log from the
/// current request. <see cref="Before"/> and <see cref="After"/> are short summaries of what changed, such as
/// "Status=Published" → "Status=Hidden"; never the content itself or anyone's personal details.
/// </summary>
public sealed record ModerationAuditEntry(
    string Module,
    string Action,
    string TargetType,
    string TargetId,
    string Reason,
    string? Before = null,
    string? After = null);

/// <summary>
/// The moderation audit log: every moderator or admin action in Posts, Profiles and Media is recorded with the
/// actor, their role, the action, the target, the reason and the time. The Posts module stores it
/// (<c>posts.ModerationAuditLog</c>) and serves <c>GET /api/admin/audit</c>; other modules record through this contract.
/// </summary>
public interface IModerationAuditLog
{
    /// <summary>Records the entry and saves it straight away.</summary>
    Task RecordAsync(ModerationAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>Names of the audited actions, shared by every module so the log can be filtered by action.</summary>
public static class ModerationActions
{
    public const string ResolveReport = "ResolveReport";
    public const string HidePost = "HidePost";
    public const string UnhidePost = "UnhidePost";
    public const string DeletePost = "DeletePost";
    public const string ApprovePost = "ApprovePost";
    public const string SetPostSensitive = "SetPostSensitive";
    public const string HideComment = "HideComment";
    public const string DeleteComment = "DeleteComment";
    public const string ApproveComment = "ApproveComment";
    public const string DecideAppeal = "DecideAppeal";
    public const string ResetProfile = "ResetProfile";
    public const string DeleteMedia = "DeleteMedia";
    public const string ApproveMedia = "ApproveMedia";
    public const string RejectMedia = "RejectMedia";
    public const string AddMediaRedactions = "AddMediaRedactions";
    public const string ViewMediaOriginal = "ViewMediaOriginal";
}

/// <summary>Who did a moderation action, from the current request.</summary>
public static class ModerationActor
{
    public const string AdminRole = StaffRoles.Admin;
    public const string ModeratorRole = StaffRoles.Moderator;

    /// <summary>A machine token issued to a trusted client, with no user.</summary>
    public const string ServiceRole = "Service";

    public static string RoleOf(ICurrentUser caller) =>
        caller.IdentityId is null ? ServiceRole
        : caller.HasRole(StaffRoles.Admin) ? AdminRole
        : ModeratorRole;
}

/// <summary>
/// Used only when no module stores the audit log (the Posts module isn't hosted): the entry is written to the
/// application log so the action isn't lost entirely.
/// </summary>
internal sealed class LoggingModerationAuditLog(ICurrentUser caller, ILogger<LoggingModerationAuditLog> logger)
    : IModerationAuditLog
{
    public Task RecordAsync(ModerationAuditEntry entry, CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Moderation audit (not stored: the Posts module isn't hosted): {Role} {ActorId} {Action} {TargetType} {TargetId} in {Module}.",
            ModerationActor.RoleOf(caller),
            caller.UserId(),
            entry.Action,
            entry.TargetType,
            entry.TargetId,
            entry.Module);
        return Task.CompletedTask;
    }
}
