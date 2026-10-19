using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Stores the moderation audit log in <c>posts.ModerationAuditLog</c>, for every module in the host. The actor, their
/// role and the time come from the current request. It shares the Posts database context, so when a Posts handler
/// records an entry before saving, the change and its audit entry are saved together.
/// </summary>
internal sealed class PostsModerationAuditLog(IModerationAuditRepository records, ICurrentUser caller, TimeProvider clock)
    : IModerationAuditLog
{
    public async Task RecordAsync(ModerationAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        records.Add(ModerationAuditRecord.Create(
            caller.UserId(),
            ModerationActor.RoleOf(caller),
            entry.Module,
            entry.Action,
            entry.TargetType,
            entry.TargetId,
            entry.Reason,
            entry.Before,
            entry.After,
            clock.GetUtcNow()));
        await records.SaveChangesAsync(cancellationToken);
    }
}
