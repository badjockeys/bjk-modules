namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A post of a driving incident with 1 to 10 photos or videos. Stored in <c>posts.Posts</c>, with its media
/// in <c>posts.PostMedia</c>. Visible to everyone as soon as it's created, because its media must already be
/// processed (Ready) by then.
/// </summary>
public sealed class Post
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 5000;
    public const int PlaceNameMaxLength = 200;
    public const int HiddenReasonMaxLength = 500;
    public const int MaxMedia = 10;

    private readonly List<PostMedia> _media = [];

    // For EF Core.
    private Post()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The author's user id (the token's "sub").</summary>
    public Guid AuthorId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public IncidentCategory Category { get; private set; }

    /// <summary>When the incident happened, which may be well before the post was created.</summary>
    public DateTimeOffset IncidentAt { get; private set; }

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    /// <summary>A readable place for the pin, such as "A56, Bury". Optional.</summary>
    public string? PlaceName { get; private set; }

    public PostStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public DateTimeOffset? HiddenAt { get; private set; }

    public string? HiddenReason { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>True while the author's account is deactivated: the post is hidden from everyone else but kept.</summary>
    public bool IsAuthorDeactivated { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The photos and videos, in the order the author chose.</summary>
    public IReadOnlyList<PostMedia> Media => _media;

    public static Post Create(Guid authorId, PostDetails details, IReadOnlyList<Guid> mediaIds, DateTimeOffset now)
    {
        if (authorId == Guid.Empty)
        {
            throw new ArgumentException("An author id is required.", nameof(authorId));
        }

        if (mediaIds.Count is 0 or > MaxMedia)
        {
            throw new ArgumentException($"A post has 1 to {MaxMedia} photos or videos.", nameof(mediaIds));
        }

        if (mediaIds.Distinct().Count() != mediaIds.Count || mediaIds.Contains(Guid.Empty))
        {
            throw new ArgumentException("Each photo or video can only be added once.", nameof(mediaIds));
        }

        var post = new Post
        {
            Id = Guid.CreateVersion7(now),
            AuthorId = authorId,
            Status = PostStatus.Published,
            CreatedAt = now,
        };
        post.SetDetails(details);
        for (var i = 0; i < mediaIds.Count; i++)
        {
            post._media.Add(new PostMedia(post.Id, mediaIds[i], i));
        }

        return post;
    }

    public bool IsOwnedBy(Guid userId) => AuthorId == userId;

    public bool IsPublished => Status == PostStatus.Published;

    /// <summary>Published and its author's account is active: anyone can see it.</summary>
    public bool IsPublic => IsPublished && !IsAuthorDeactivated;

    public void Update(PostDetails details, DateTimeOffset now)
    {
        EnsureNotDeleted();
        SetDetails(details);
        UpdatedAt = now;
    }

    public void Hide(string reason, DateTimeOffset now)
    {
        EnsureNotDeleted();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = PostStatus.Hidden;
        HiddenReason = reason.Length <= HiddenReasonMaxLength ? reason : reason[..HiddenReasonMaxLength];
        HiddenAt = now;
        UpdatedAt = now;
    }

    public void Unhide(DateTimeOffset now)
    {
        EnsureNotDeleted();
        Status = PostStatus.Published;
        HiddenReason = null;
        HiddenAt = null;
        UpdatedAt = now;
    }

    public void Delete(DateTimeOffset now)
    {
        if (Status == PostStatus.Deleted)
        {
            return;
        }

        Status = PostStatus.Deleted;
        DeletedAt = now;
        UpdatedAt = now;
    }

    private void SetDetails(PostDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(details.Title);
        if (details.Latitude is < -90 or > 90 || details.Longitude is < -180 or > 180)
        {
            throw new ArgumentException("The location isn't a valid latitude and longitude.", nameof(details));
        }

        Title = Limit(details.Title.Trim(), TitleMaxLength);
        Description = string.IsNullOrWhiteSpace(details.Description) ? null : Limit(details.Description.Trim(), DescriptionMaxLength);
        Category = details.Category;
        IncidentAt = details.IncidentAt;
        Latitude = decimal.Round(details.Latitude, 6);
        Longitude = decimal.Round(details.Longitude, 6);
        PlaceName = string.IsNullOrWhiteSpace(details.PlaceName) ? null : Limit(details.PlaceName.Trim(), PlaceNameMaxLength);
    }

    private void EnsureNotDeleted()
    {
        if (Status == PostStatus.Deleted)
        {
            throw new InvalidOperationException("The post has been deleted.");
        }
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : throw new ArgumentException($"Must be {maxLength} characters or fewer.", nameof(value));
}

/// <summary>The parts of a post its author writes and can later edit.</summary>
public sealed record PostDetails(
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName);

/// <summary>One photo or video (from the Media module) in a post.</summary>
public sealed class PostMedia
{
    // For EF Core.
    private PostMedia()
    {
    }

    internal PostMedia(Guid postId, Guid mediaId, int position)
    {
        PostId = postId;
        MediaId = mediaId;
        Position = position;
    }

    public Guid PostId { get; private set; }

    public Guid MediaId { get; private set; }

    /// <summary>0-based order within the post.</summary>
    public int Position { get; private set; }
}
