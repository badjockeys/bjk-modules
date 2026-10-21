using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Uploads;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Profiles.Infrastructure;

/// <summary>Tells the Media module which uploads are someone's avatar, so they're kept.</summary>
internal sealed class ProfilesMediaUsage(ProfilesDbContext db) : IMediaUsage
{
    public string Name => "avatars";

    public async Task<IReadOnlySet<Guid>> InUseAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var avatars = await db.Profiles.AsNoTracking()
            .Where(p => p.AvatarMediaId != null && mediaIds.Contains(p.AvatarMediaId.Value))
            .Select(p => p.AvatarMediaId!.Value)
            .ToListAsync(cancellationToken);
        return avatars.ToHashSet();
    }
}
