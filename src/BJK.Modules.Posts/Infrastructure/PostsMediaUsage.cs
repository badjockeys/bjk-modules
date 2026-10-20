using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Uploads;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>Tells the Media module which uploads are in a post (whatever its state) or a draft, so they're kept.</summary>
internal sealed class PostsMediaUsage(PostsDbContext db) : IMediaUsage
{
    public string Name => "posts and drafts";

    public async Task<IReadOnlySet<Guid>> InUseAsync(IReadOnlyCollection<Guid> mediaIds, CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var inPosts = await db.Set<PostMedia>().AsNoTracking()
            .Where(m => mediaIds.Contains(m.MediaId))
            .Select(m => m.MediaId)
            .ToListAsync(cancellationToken);
        var inDrafts = await db.Set<DraftMedia>().AsNoTracking()
            .Where(m => mediaIds.Contains(m.MediaId))
            .Select(m => m.MediaId)
            .ToListAsync(cancellationToken);
        return inPosts.Concat(inDrafts).ToHashSet();
    }
}
