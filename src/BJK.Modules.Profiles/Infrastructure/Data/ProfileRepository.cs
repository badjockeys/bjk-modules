using BJK.Modules.Profiles.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Profiles.Infrastructure.Data;

public interface IProfileRepository
{
    Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Looks up a normalised handle.</summary>
    Task<Profile?> GetByHandleAsync(string handle, CancellationToken cancellationToken);

    Task<IReadOnlyList<Profile>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    void Add(Profile profile);

    void Remove(Profile profile);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class ProfileRepository(ProfilesDbContext db) : IProfileRepository
{
    public Task<Profile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Profile?> GetByHandleAsync(string handle, CancellationToken cancellationToken) =>
        db.Profiles.FirstOrDefaultAsync(p => p.Handle == handle, cancellationToken);

    public async Task<IReadOnlyList<Profile>> GetManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken) =>
        await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

    public void Add(Profile profile) => db.Profiles.Add(profile);

    public void Remove(Profile profile) => db.Profiles.Remove(profile);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
