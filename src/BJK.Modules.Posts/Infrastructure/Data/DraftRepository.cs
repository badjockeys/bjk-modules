using BJK.Modules.Posts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Posts.Infrastructure.Data;

/// <summary>Saved drafts of posts. Shares <see cref="PostsDbContext"/> with the posts, so saving either saves both.</summary>
public interface IDraftRepository
{
    /// <summary>A draft with its media, tracked so changes can be saved. Null if it doesn't exist.</summary>
    Task<PostDraft?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>An author's drafts, last saved first.</summary>
    Task<IReadOnlyList<PostDraft>> ListForAuthorAsync(Guid authorId, CancellationToken cancellationToken);

    /// <summary>How many drafts the author has.</summary>
    Task<int> CountForAuthorAsync(Guid authorId, CancellationToken cancellationToken);

    /// <summary>Which of these media are in a draft other than the ones in <paramref name="exceptDraftIds"/>.</summary>
    Task<IReadOnlySet<Guid>> MediaInOtherDraftsAsync(
        IReadOnlyCollection<Guid> mediaIds,
        IReadOnlyCollection<Guid> exceptDraftIds,
        CancellationToken cancellationToken);

    /// <summary>Drafts last saved before <paramref name="savedBefore"/>, oldest first, tracked.</summary>
    Task<IReadOnlyList<PostDraft>> ListExpiredAsync(DateTimeOffset savedBefore, int max, CancellationToken cancellationToken);

    /// <summary>Deletes every draft of a user straight away (their account was deleted).</summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(PostDraft draft);

    void Remove(PostDraft draft);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

internal sealed class DraftRepository(PostsDbContext db) : IDraftRepository
{
    public Task<PostDraft?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.PostDrafts.Include(d => d.Media).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

    public async Task<IReadOnlyList<PostDraft>> ListForAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
        await db.PostDrafts.AsNoTracking()
            .Include(d => d.Media)
            .Where(d => d.AuthorId == authorId)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(cancellationToken);

    public Task<int> CountForAuthorAsync(Guid authorId, CancellationToken cancellationToken) =>
        db.PostDrafts.CountAsync(d => d.AuthorId == authorId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> MediaInOtherDraftsAsync(
        IReadOnlyCollection<Guid> mediaIds,
        IReadOnlyCollection<Guid> exceptDraftIds,
        CancellationToken cancellationToken)
    {
        if (mediaIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var used = await db.Set<DraftMedia>().AsNoTracking()
            .Where(m => mediaIds.Contains(m.MediaId) && !exceptDraftIds.Contains(m.DraftId))
            .Select(m => m.MediaId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return used.ToHashSet();
    }

    public async Task<IReadOnlyList<PostDraft>> ListExpiredAsync(
        DateTimeOffset savedBefore,
        int max,
        CancellationToken cancellationToken) =>
        await db.PostDrafts
            .Include(d => d.Media)
            .Where(d => d.UpdatedAt < savedBefore)
            .OrderBy(d => d.UpdatedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        // DraftMedia rows go with them (cascade).
        db.PostDrafts.Where(d => d.AuthorId == userId).ExecuteDeleteAsync(cancellationToken);

    public void Add(PostDraft draft) => db.PostDrafts.Add(draft);

    public void Remove(PostDraft draft) => db.PostDrafts.Remove(draft);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class PostDraftConfiguration : IEntityTypeConfiguration<PostDraft>
{
    public void Configure(EntityTypeBuilder<PostDraft> builder)
    {
        builder.ToTable("Drafts");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Title).HasMaxLength(Post.TitleMaxLength);
        builder.Property(d => d.Description).HasMaxLength(Post.DescriptionInputMaxLength);
        builder.Property(d => d.Category).HasConversion<string>().HasMaxLength(40);
        builder.Property(d => d.Latitude).HasPrecision(9, 6);
        builder.Property(d => d.Longitude).HasPrecision(9, 6);
        builder.Property(d => d.PlaceName).HasMaxLength(Post.PlaceNameMaxLength);
        builder.Property(d => d.PassengersText).HasColumnName("Passengers").HasMaxLength(PostDraft.PassengersMaxLength);
        builder.Property(d => d.VehicleType).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.VehicleColour).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.VehicleMake).HasMaxLength(VehicleCatalogue.NameMaxLength);
        builder.Property(d => d.VehicleModel).HasMaxLength(VehicleCatalogue.NameMaxLength);
        builder.Property(d => d.RowVersion).IsRowVersion();
        builder.Ignore(d => d.Types);
        builder.Ignore(d => d.Passengers);
        builder.Ignore(d => d.ExpiresAt);

        builder.HasMany(d => d.Media)
            .WithOne()
            .HasForeignKey(m => m.DraftId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(d => d.Media).UsePropertyAccessMode(PropertyAccessMode.Field);

        // An author's drafts (newest first), and the expiry sweep.
        builder.HasIndex(d => new { d.AuthorId, d.UpdatedAt });
        builder.HasIndex(d => d.UpdatedAt);
    }
}

internal sealed class DraftMediaConfiguration : IEntityTypeConfiguration<DraftMedia>
{
    public void Configure(EntityTypeBuilder<DraftMedia> builder)
    {
        builder.ToTable("DraftMedia");
        builder.HasKey(m => new { m.DraftId, m.MediaId });
        builder.HasIndex(m => m.MediaId);
    }
}
