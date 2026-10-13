using BJK.Modules.User.Domain;

namespace BJK.Modules.User.Infrastructure.Services;

public interface IUserRepository
{
    Task<UserProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> IdentityExistsAsync(string identityId, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    void Add(UserProfile user);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
