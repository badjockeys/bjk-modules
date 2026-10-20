namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A post the author has started and saved to finish later, from any device. Nothing in it is checked the way a post
/// is (it can be half finished); it's only private storage for the composer. The photos and videos stay attached.
/// Each author can keep up to <see cref="MaxPerAuthor"/>, and a draft is deleted (with its media) once it hasn't been
/// saved for <see cref="Lifetime"/>. Publishing a post from a draft deletes the draft but keeps its media.
/// </summary>
public sealed class PostDraft
{
    /// <summary>How many drafts one author can keep.</summary>
    public const int MaxPerAuthor = 5;

    /// <summary>Passengers' @handles, joined with commas: 4 handles of up to 30 characters, with room to spare.</summary>
    public const int PassengersMaxLength = 200;

    /// <summary>How long a draft is kept after it was last saved.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly List<DraftMedia> _media = [];

    // For EF Core.
    private PostDraft()
    {
    }

    public Guid Id { get; private set; }

    public Guid AuthorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it was last saved. The draft expires <see cref="Lifetime"/> after this.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public string? Title { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The main type of bad driving (the first chosen), or null if none is chosen yet.</summary>
    public IncidentCategory? Category { get; private set; }

    /// <summary>Every type chosen, as bits (see <see cref="IncidentTypeFlags"/>).</summary>
    public int TypeFlags { get; private set; }

    public DateTimeOffset? IncidentAt { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public string? PlaceName { get; private set; }

    public bool IsSensitive { get; private set; }

    /// <summary>Passengers' @handles as typed (not yet checked), joined with commas. See <see cref="Passengers"/>.</summary>
    public string? PassengersText { get; private set; }

    public VehicleType? VehicleType { get; private set; }

    public string? VehicleMake { get; private set; }

    public string? VehicleModel { get; private set; }

    public VehicleColour? VehicleColour { get; private set; }

    public bool? ReportedToPolice { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The photos or video (order by <see cref="DraftMedia.Position"/>).</summary>
    public IReadOnlyList<DraftMedia> Media => _media;

    /// <summary>Every type chosen, the main one first.</summary>
    public IReadOnlyList<IncidentCategory> Types =>
        Category is { } main ? IncidentTypeFlags.ToList(TypeFlags, main) : [];

    /// <summary>Passengers' @handles, as typed.</summary>
    public IReadOnlyList<string> Passengers =>
        string.IsNullOrEmpty(PassengersText) ? [] : PassengersText.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>When it will be deleted if it isn't saved again.</summary>
    public DateTimeOffset ExpiresAt => UpdatedAt + Lifetime;

    /// <summary>A new draft.</summary>
    /// <param name="authorId">Who it belongs to.</param>
    /// <param name="content">What's in it so far.</param>
    /// <param name="now">The current time.</param>
    public static PostDraft Create(Guid authorId, DraftContent content, DateTimeOffset now)
    {
        var draft = new PostDraft { Id = Guid.NewGuid(), AuthorId = authorId, CreatedAt = now };
        draft.Update(content, now);
        return draft;
    }

    /// <summary>Replaces everything in the draft with <paramref name="content"/>.</summary>
    /// <param name="content">What's in it now.</param>
    /// <param name="now">The current time.</param>
    public void Update(DraftContent content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        Title = Clean(content.Title);
        Description = Clean(content.Description);
        var types = content.Types.Distinct().ToList();
        Category = types.Count > 0 ? types[0] : null;
        TypeFlags = IncidentTypeFlags.From(types);
        IncidentAt = content.IncidentAt;
        Latitude = content.Latitude;
        Longitude = content.Longitude;
        PlaceName = Clean(content.PlaceName);
        IsSensitive = content.IsSensitive;
        var handles = content.Passengers
            .Select(h => h.Trim().TrimStart('@'))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        PassengersText = handles.Count > 0 ? string.Join(',', handles) : null;
        VehicleType = content.VehicleType;
        VehicleMake = Clean(content.VehicleMake);
        VehicleModel = Clean(content.VehicleModel);
        VehicleColour = content.VehicleColour;
        ReportedToPolice = content.ReportedToPolice;
        SetMedia(content.MediaIds);
        UpdatedAt = now;
    }

    private void SetMedia(IReadOnlyList<Guid> mediaIds)
    {
        // Change the rows in place rather than replacing them, so a photo kept in the draft keeps its row.
        var wanted = mediaIds.Distinct().ToList();
        _media.RemoveAll(m => !wanted.Contains(m.MediaId));
        for (var position = 0; position < wanted.Count; position++)
        {
            var existing = _media.Find(m => m.MediaId == wanted[position]);
            if (existing is null)
            {
                _media.Add(new DraftMedia(Id, wanted[position], position));
            }
            else
            {
                existing.MoveTo(position);
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A photo or video in a draft.</summary>
public sealed class DraftMedia
{
    // For EF Core.
    private DraftMedia()
    {
    }

    internal DraftMedia(Guid draftId, Guid mediaId, int position)
    {
        DraftId = draftId;
        MediaId = mediaId;
        Position = position;
    }

    public Guid DraftId { get; private set; }

    public Guid MediaId { get; private set; }

    /// <summary>0-based order within the draft.</summary>
    public int Position { get; private set; }

    internal void MoveTo(int position) => Position = position;
}

/// <summary>Everything a draft can hold. All of it is optional.</summary>
/// <param name="Title">The title so far.</param>
/// <param name="Description">"What happened?" so far.</param>
/// <param name="Types">Types of bad driving chosen, the main one first.</param>
/// <param name="IncidentAt">When it happened.</param>
/// <param name="Latitude">Where it happened.</param>
/// <param name="Longitude">Where it happened.</param>
/// <param name="PlaceName">The road and town.</param>
/// <param name="IsSensitive">Marked as graphic.</param>
/// <param name="Passengers">Passengers' @handles, as typed.</param>
/// <param name="VehicleType">The vehicle driven badly.</param>
/// <param name="VehicleMake">Its make, from the list.</param>
/// <param name="VehicleModel">Its model, from the list.</param>
/// <param name="VehicleColour">Its colour.</param>
/// <param name="ReportedToPolice">Whether it was reported to the police.</param>
/// <param name="MediaIds">The uploaded photos or video, in order.</param>
public sealed record DraftContent(
    string? Title,
    string? Description,
    IReadOnlyList<IncidentCategory> Types,
    DateTimeOffset? IncidentAt,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceName,
    bool IsSensitive,
    IReadOnlyList<string> Passengers,
    VehicleType? VehicleType,
    string? VehicleMake,
    string? VehicleModel,
    VehicleColour? VehicleColour,
    bool? ReportedToPolice,
    IReadOnlyList<Guid> MediaIds);
