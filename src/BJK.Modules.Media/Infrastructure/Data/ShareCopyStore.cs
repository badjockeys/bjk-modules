using BJK.Modules.Media.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Data;

/// <summary>The vertical copies of videos made for sharing (<c>media.ShareCopies</c>).</summary>
public interface IShareCopyStore
{
    /// <summary>The media's copy, tracked so changes can be saved; null if none was ever asked for.</summary>
    Task<ShareCopy?> GetAsync(Guid mediaId, CancellationToken cancellationToken);

    void Add(ShareCopy copy);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class ShareCopyStore(MediaDbContext db) : IShareCopyStore
{
    public Task<ShareCopy?> GetAsync(Guid mediaId, CancellationToken cancellationToken) =>
        db.ShareCopies.SingleOrDefaultAsync(c => c.MediaId == mediaId, cancellationToken);

    public void Add(ShareCopy copy) => db.ShareCopies.Add(copy);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
