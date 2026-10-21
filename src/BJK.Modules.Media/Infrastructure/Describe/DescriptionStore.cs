using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Queues;
using Microsoft.EntityFrameworkCore;

namespace BJK.Modules.Media.Infrastructure.Describe;

/// <summary>"Suggest a description" requests (<c>media.Descriptions</c>).</summary>
public interface IDescriptionStore
{
    Task<MediaDescription?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>How many the person has asked for since <paramref name="since"/>.</summary>
    Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    void Add(MediaDescription description);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Saves and queues messages in one transaction.</summary>
    Task SaveChangesWithMessagesAsync(Func<CancellationToken, Task> queueMessages, CancellationToken cancellationToken);
}

internal sealed class DescriptionStore(MediaDbContext db) : IDescriptionStore
{
    public Task<MediaDescription?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Descriptions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.Descriptions.CountAsync(d => d.UserId == userId && d.CreatedAt >= since, cancellationToken);

    public void Add(MediaDescription description) => db.Descriptions.Add(description);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task SaveChangesWithMessagesAsync(Func<CancellationToken, Task> queueMessages, CancellationToken cancellationToken) =>
        db.SaveChangesWithMessagesAsync(queueMessages, cancellationToken);
}

/// <summary>The job on the <c>Queues:MediaDescriptions</c> queue.</summary>
/// <param name="DescriptionId">The request.</param>
public sealed record DescriptionJob(Guid DescriptionId);
