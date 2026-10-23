using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Members' household cars (<c>posts.HouseholdCars</c>).</summary>
public interface IHouseholdCarRepository
{
    /// <summary>The member's cars, oldest first.</summary>
    Task<IReadOnlyList<HouseholdCar>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>A car (tracked), or null.</summary>
    Task<HouseholdCar?> GetAsync(Guid id, CancellationToken cancellationToken);

    void Add(HouseholdCar car);

    void Remove(HouseholdCar car);

    /// <summary>Gives the posts filmed from this car its new nickname.</summary>
    Task RenameOnPostsAsync(Guid carId, string nickname, CancellationToken cancellationToken);

    /// <summary>Deletes all of a member's cars (their account was deleted).</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class HouseholdCarRepository(PostsDbContext db) : IHouseholdCarRepository
{
    public async Task<IReadOnlyList<HouseholdCar>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.HouseholdCars.Where(c => c.UserId == userId).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);

    public Task<HouseholdCar?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.HouseholdCars.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public void Add(HouseholdCar car) => db.HouseholdCars.Add(car);

    public void Remove(HouseholdCar car) => db.HouseholdCars.Remove(car);

    public Task RenameOnPostsAsync(Guid carId, string nickname, CancellationToken cancellationToken) =>
        db.Posts.Where(p => p.FilmedFromCarId == carId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.FilmedFrom, nickname), cancellationToken);

    public Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.HouseholdCars.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
