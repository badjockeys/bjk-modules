using BJK.Modules.User.Domain;

namespace BJK.Modules.User.Infrastructure.Services;

public interface IUserRepository
{
    Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<UserProfile?> GetByIdentityIdAsync(string identityId, CancellationToken cancellationToken);

    /// <summary>Active users, newest first.</summary>
    Task<(IReadOnlyList<UserProfile> Items, int TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    void Add(UserProfile user);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
