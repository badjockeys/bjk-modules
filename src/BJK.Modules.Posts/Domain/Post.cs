using BJK.Modules.Shared.Notifications;
using BJK.Modules.Safety;

namespace BJK.Modules.Posts.Domain;

/// <summary>
/// A post of a driving incident with 1 to 10 photos or one video. Stored in <c>posts.Posts</c>, with its media
/// in <c>posts.PostMedia</c>. If its media are already Ready it's visible to everyone as soon as it's created (unless
/// content screening holds it for review); otherwise it waits, visible only to its author and moderators
/// (<see cref="PostMediaState.Pending"/>), and is published automatically when they're all Ready. Its map pin is
/// rounded to about 500 m (<see cref="PinRounding"/>).
/// </summary>
public sealed class Post
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 5000;

    /// <summary>
    /// The most a new or edited description may hold (the composer's counter). Older posts can be longer, up to
    /// <see cref="DescriptionMaxLength"/>, and keep their text.
    /// </summary>
    public const int DescriptionInputMaxLength = 500;
    public const int PlaceNameMaxLength = 200;
    public const int HiddenReasonMaxLength = 500;
    public const int MediaFailureReasonMaxLength = 500;
    public const int NotifyEmailMaxLength = 254;
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

    /// <summary>The main type of bad driving: the first one the author chose. See <see cref="Types"/> for all of them.</summary>
    public IncidentCategory Category { get; private set; }

    /// <summary>Every type the author chose, as bits (see <see cref="IncidentTypeFlags"/>).</summary>
    public int TypeFlags { get; private set; }

    /// <summary>Every type the author chose, the main one first.</summary>
    public IReadOnlyList<IncidentCategory> Types => IncidentTypeFlags.ToList(TypeFlags, Category);

    /// <summary>The kind of vehicle driven badly, if the author said. Required once any vehicle detail is given.</summary>
    public VehicleType? VehicleType { get; private set; }

    /// <summary>A make from <see cref="VehicleCatalogue"/>, if given.</summary>
    public string? VehicleMake { get; private set; }

    /// <summary>A model of <see cref="VehicleMake"/> from <see cref="VehicleCatalogue"/>, if given.</summary>
    public string? VehicleModel { get; private set; }

    public VehicleColour? VehicleColour { get; private set; }

    /// <summary>The author reported it to the police (true), didn't (false), or didn't say (null).</summary>
    public bool? ReportedToPolice { get; private set; }

    /// <summary>Who can comment, chosen by the author. Everyone by default.</summary>
    public CommentSetting CommentSetting { get; private set; }

    /// <summary>
    /// The sun was down at the incident's time and place (<see cref="Daylight"/>). Worked out when the details are
    /// saved; null for posts saved before it existed.
    /// </summary>
    public bool? IsDark { get; private set; }

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

    /// <summary>
    /// Graphic content (injuries, serious crashes), set by the author, a moderator or automatic detection
    /// (<see cref="SensitiveSource"/>). Sensitive posts are shown to everyone, but their photos and video only blurred,
    /// except to age-verified adults, the author and moderators.
    /// </summary>
    public bool IsSensitive { get; private set; }

    /// <summary>Who set the sensitive tag (None when it isn't tagged).</summary>
    public PostSensitiveSource SensitiveSource { get; private set; }

    /// <summary>The highest graphic content score automatic detection gave its media, when it tagged the post (or tried to).</summary>
    public double? SensitiveScore { get; private set; }

    /// <summary>Moderator review of an automatic tag. Kept with the score as a record of the outcome, to tune detection.</summary>
    public SensitiveReviewStatus SensitiveReview { get; private set; }

    /// <summary>When a moderator confirmed or undid the automatic tag.</summary>
    public DateTimeOffset? SensitiveReviewedAt { get; private set; }

    /// <summary>Who confirmed or undid the automatic tag (null for a machine token).</summary>
    public Guid? SensitiveReviewedBy { get; private set; }

    /// <summary>
    /// The author may remove the tag only if they set it (not a moderator's, nor an automatic one) and automatic
    /// detection hasn't also flagged the media (<see cref="SensitiveScore"/>).
    /// </summary>
    public bool AuthorMayRemoveSensitive =>
        SensitiveSource is PostSensitiveSource.None or PostSensitiveSource.Author && SensitiveScore is null;

    /// <summary>Content screening flagged the text for a moderator to look at. Cleared when a moderator approves it.</summary>
    public bool IsFlagged { get; private set; }

    /// <summary>The latest screening score, 0 to 1.</summary>
    public double? ScreeningScore { get; private set; }

    /// <summary>Why the text scored as it did, comma-separated (threat, insult...).</summary>
    public string? ScreeningLabels { get; private set; }

    /// <summary>When a moderator last approved the post after screening.</summary>
    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    /// <summary>
    /// Whether the photos and video are ready. Others see the post only when it's <see cref="PostStatus.Published"/>
    /// and this is <see cref="PostMediaState.Ready"/>.
    /// </summary>
    public PostMediaState MediaState { get; private set; }

    /// <summary>Why the post couldn't be published (when <see cref="MediaState"/> is Failed), for its author.</summary>
    public string? MediaFailureReason { get; private set; }

    /// <summary>When every photo and video became ready (null if they were ready when the post was created).</summary>
    public DateTimeOffset? MediaReadyAt { get; private set; }

    /// <summary>
    /// Where to email the author if the post can't be published: the address in their token when they posted, kept
    /// only while the post is Pending and cleared as soon as it's published or they've been told.
    /// </summary>
    public string? NotifyEmail { get; private set; }

    /// <summary>
    /// The post is one video (a post has 1 to 10 photos or one video), so it's also a reel. Set when the post is
    /// created; a post's media never change afterwards.
    /// </summary>
    public bool HasVideo { get; private set; }

    /// <summary>
    /// Ranking ("recent and popular") for reels and the For you timeline: (1 + reactions + 2 × comments) / (age in
    /// hours + 2)^1.5 (<see cref="ReelRanking"/>). Refreshed in the database every few minutes by the reels background
    /// job for every post; 0 once a post is older than the ranking window.
    /// </summary>
    public double ReelScore { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// When the post's vote closes (from the author's <see cref="VoteDuration"/> setting at the time they posted);
    /// null when it never closes. Votes count until then, and the result is announced to the author and followers.
    /// </summary>
    public DateTimeOffset? VoteClosesAt { get; private set; }

    /// <summary>When the closed vote's result was announced; null until then (or for votes that never close).</summary>
    public DateTimeOffset? VoteResultSentAt { get; private set; }

    /// <summary>Which question the post's vote asks, from its types.</summary>
    public VoteKind VoteKind => PostVoting.KindFor(Types);

    /// <summary>True once the vote has closed.</summary>
    public bool IsVoteClosed(DateTimeOffset now) => VoteClosesAt is { } closes && closes <= now;

    /// <summary>Sets when the vote closes, from the author's setting. Called once, when the post is made.</summary>
    public void SetVoteDuration(VoteDuration duration) => VoteClosesAt = PostVoting.ClosesAt(CreatedAt, duration);

    /// <summary>Records that the closed vote's result has been announced, so it's never announced twice.</summary>
    public void MarkVoteResultSent(DateTimeOffset now) => VoteResultSentAt ??= now;


    /// <summary>The photos and videos, in the order the author chose.</summary>
    public IReadOnlyList<PostMedia> Media => _media;

    /// <param name="authorId">The author's user id.</param>
    /// <param name="details">Title, description, category, time and place.</param>
    /// <param name="mediaIds">The photos or video, in order.</param>
    /// <param name="now">The time.</param>
    /// <param name="isSensitive">Graphic content.</param>
    /// <param name="mediaPending">
    /// True if any of the media is still being processed: the post waits (<see cref="PostMediaState.Pending"/>).
    /// </param>
    /// <param name="notifyEmail">For a pending post: where to tell the author if it can't be published.</param>
    /// <param name="hasVideo">The post is one video (a reel).</param>
    public static Post Create(
        Guid authorId,
        PostDetails details,
        IReadOnlyList<Guid> mediaIds,
        DateTimeOffset now,
        bool isSensitive = false,
        bool mediaPending = false,
        string? notifyEmail = null,
        bool hasVideo = false)
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
            IsSensitive = isSensitive,
            SensitiveSource = isSensitive ? PostSensitiveSource.Author : PostSensitiveSource.None,
            MediaState = mediaPending ? PostMediaState.Pending : PostMediaState.Ready,
            HasVideo = hasVideo,
            ReelScore = ReelRanking.Score(0, 0, TimeSpan.Zero),
            NotifyEmail = mediaPending && !string.IsNullOrWhiteSpace(notifyEmail) && notifyEmail.Trim().Length <= NotifyEmailMaxLength
                ? notifyEmail.Trim()
                : null,
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

    /// <summary>Published, its media are ready and its author's account is active: anyone can see it.</summary>
    public bool IsPublic => IsPublished && MediaState == PostMediaState.Ready && !IsAuthorDeactivated;

    /// <summary>What area alerts need to know about the post, once it's public.</summary>
    /// <param name="now">When it became public.</param>
    public PublishedPost ToPublished(DateTimeOffset now) =>
        new(Id, AuthorId, Title, PlaceName, Latitude, Longitude, now);

    /// <summary>Still waiting for its photos or video to finish processing.</summary>
    public bool IsMediaPending => MediaState == PostMediaState.Pending;

    /// <summary>
    /// Every photo and video is ready: the post is published (to others too, if its status allows). Only from
    /// Pending; returns false if there was nothing to do (already ready, failed or deleted), so repeats are harmless.
    /// </summary>
    /// <param name="now">The time.</param>
    public bool MarkMediaReady(DateTimeOffset now)
    {
        if (Status == PostStatus.Deleted || MediaState != PostMediaState.Pending)
        {
            return false;
        }

        MediaState = PostMediaState.Ready;
        MediaReadyAt = now;
        NotifyEmail = null;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// A photo or video can never be ready: the post stays unpublished, with the reason for its author. Only from
    /// Pending; returns false if there was nothing to do, so repeats are harmless (and the author is emailed once).
    /// </summary>
    /// <param name="reason">Why, in words the author understands.</param>
    /// <param name="now">The time.</param>
    public bool MarkMediaFailed(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status == PostStatus.Deleted || MediaState != PostMediaState.Pending)
        {
            return false;
        }

        MediaState = PostMediaState.Failed;
        MediaFailureReason = reason.Length <= MediaFailureReasonMaxLength ? reason : reason[..MediaFailureReasonMaxLength];
        UpdatedAt = now;
        return true;
    }

    /// <summary>Forgets the author's email address once they've been told (or it couldn't be sent).</summary>
    public void ClearNotifyEmail() => NotifyEmail = null;

    /// <summary>The author changes who can comment. Comments already there stay.</summary>
    public void SetCommentSetting(CommentSetting setting, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (CommentSetting == setting)
        {
            return;
        }

        CommentSetting = setting;
        UpdatedAt = now;
    }

    public void Update(PostDetails details, DateTimeOffset now)
    {
        EnsureNotDeleted();
        SetDetails(details);
        UpdatedAt = now;
    }

    /// <summary>
    /// Marks the post as sensitive (graphic) or not. A moderator doing so also settles any pending review of an
    /// automatic tag (Confirmed if they keep it, Undone if they remove it).
    /// </summary>
    /// <param name="isSensitive">Tagged or not.</param>
    /// <param name="now">The time.</param>
    /// <param name="source">Who: the author (the default) or a moderator.</param>
    /// <param name="moderatorId">For a moderator: who, or null for a machine token.</param>
    public void SetSensitive(bool isSensitive, DateTimeOffset now, PostSensitiveSource source = PostSensitiveSource.Author, Guid? moderatorId = null)
    {
        EnsureNotDeleted();
        if (source == PostSensitiveSource.Moderator && SensitiveReview == SensitiveReviewStatus.Pending)
        {
            RecordReview(isSensitive, moderatorId, now);
        }

        if (IsSensitive == isSensitive)
        {
            return;
        }

        IsSensitive = isSensitive;
        SensitiveSource = isSensitive ? source : PostSensitiveSource.None;
        UpdatedAt = now;
    }

    /// <summary>
    /// Automatic detection found graphic content in the post's media: it's tagged sensitive (source Automatic) and
    /// waits for a moderator to confirm or undo it. Returns true if the post was tagged now. Does nothing if it's
    /// already tagged (the score is still recorded) or a moderator already undid an automatic tag on it. Safe to repeat.
    /// </summary>
    /// <param name="score">The highest score of its media.</param>
    /// <param name="now">The time.</param>
    public bool MarkSensitiveAutomatically(double? score, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (score is { } value)
        {
            SensitiveScore = Math.Max(SensitiveScore ?? 0, Math.Round(value, 4));
        }

        if (IsSensitive || SensitiveReview == SensitiveReviewStatus.Undone)
        {
            return false;
        }

        IsSensitive = true;
        SensitiveSource = PostSensitiveSource.Automatic;
        SensitiveReview = SensitiveReviewStatus.Pending;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// A moderator confirms an automatic sensitive tag, or undoes it (the post is then not sensitive, and detection
    /// won't tag it again). Only while the review is pending.
    /// </summary>
    /// <param name="confirm">True to keep the tag.</param>
    /// <param name="moderatorId">The moderator, or null for a machine token.</param>
    /// <param name="now">The time.</param>
    public void ReviewAutomaticSensitive(bool confirm, Guid? moderatorId, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (SensitiveReview != SensitiveReviewStatus.Pending)
        {
            throw new InvalidOperationException("The post has no automatic sensitive tag waiting for review.");
        }

        RecordReview(confirm, moderatorId, now);
        if (!confirm)
        {
            IsSensitive = false;
            SensitiveSource = PostSensitiveSource.None;
        }
        else
        {
            SensitiveSource = PostSensitiveSource.Automatic;
        }

        UpdatedAt = now;
    }

    private void RecordReview(bool confirmed, Guid? moderatorId, DateTimeOffset now)
    {
        SensitiveReview = confirmed ? SensitiveReviewStatus.Confirmed : SensitiveReviewStatus.Undone;
        SensitiveReviewedAt = now;
        SensitiveReviewedBy = moderatorId;
    }

    /// <summary>
    /// Applies a content screening result to the post's text. <see cref="ScreeningVerdict.Hold"/> holds a published
    /// post for review (only its author sees it); <see cref="ScreeningVerdict.Flag"/> flags it. A flag stays until a
    /// moderator approves the post, even if a later edit scores lower.
    /// </summary>
    public void ApplyScreening(ScreeningResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureNotDeleted();
        ScreeningScore = Math.Round(result.Score, 3);
        ScreeningLabels = ScreeningText.Labels(result.Labels);
        if (result.Verdict == ScreeningVerdict.Allow)
        {
            return;
        }

        IsFlagged = true;
        if (result.Verdict == ScreeningVerdict.Hold && Status == PostStatus.Published)
        {
            Status = PostStatus.HeldForReview;
        }
    }

    /// <summary>A moderator approves the post after screening: it's published (if held) and the flag is cleared.</summary>
    public void Approve(Guid? moderatorId, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (Status == PostStatus.HeldForReview)
        {
            Status = PostStatus.Published;
        }

        IsFlagged = false;
        ReviewedAt = now;
        ReviewedBy = moderatorId;
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
        var types = IncidentTypeFlags.Normalise(details.Types, details.Category);
        if (types.Any(t => !Enum.IsDefined(t)))
        {
            throw new ArgumentException("Unknown type of bad driving.", nameof(details));
        }

        Category = types[0];
        TypeFlags = IncidentTypeFlags.From(types);

        var vehicle = details.Vehicle ?? VehicleDetails.None;
        var make = vehicle.Make is null ? null : VehicleCatalogue.Make(vehicle.Make)
            ?? throw new ArgumentException("Unknown make.", nameof(details));
        var model = vehicle.Model is null ? null : VehicleCatalogue.Model(make, vehicle.Model)
            ?? throw new ArgumentException("Unknown model for that make.", nameof(details));
        if (vehicle.Type is null && (make is not null || model is not null || vehicle.Colour is not null))
        {
            throw new ArgumentException("Choose the type of vehicle.", nameof(details));
        }

        VehicleType = vehicle.Type;
        VehicleMake = make;
        VehicleModel = model;
        VehicleColour = vehicle.Colour;
        ReportedToPolice = details.ReportedToPolice;
        IncidentAt = details.IncidentAt;
        (Latitude, Longitude) = PinRounding.Round(details.Latitude, details.Longitude);
        PlaceName = string.IsNullOrWhiteSpace(details.PlaceName) ? null : Limit(details.PlaceName.Trim(), PlaceNameMaxLength);
        IsDark = Daylight.IsDark(details.IncidentAt, details.Latitude, details.Longitude);
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

/// <summary>
/// Rounds map pins to about 500 m, so a post never pinpoints a home or workplace. The latitude is snapped to a grid of
/// <see cref="LatitudeStep"/> degrees (500 m north–south everywhere); the longitude to a grid of
/// <see cref="LatitudeStep"/> / cos(rounded latitude) degrees, so cells are about 500 m east–west too. Rounding is
/// half away from zero, the same as SQL Server's ROUND, which the OnlineSafety migration uses on existing rows.
/// Rounding a rounded pin gives the same pin.
/// </summary>
public static class PinRounding
{
    /// <summary>500 m in degrees of latitude (one degree is about 111.32 km).</summary>
    public const decimal LatitudeStep = 0.0045m;

    // Stops the longitude step blowing up near the poles.
    private const double MinCosine = 0.01;

    public static (decimal Latitude, decimal Longitude) Round(decimal latitude, decimal longitude)
    {
        var lat = decimal.Round(
            decimal.Round(latitude / LatitudeStep, 0, MidpointRounding.AwayFromZero) * LatitudeStep,
            6,
            MidpointRounding.AwayFromZero);
        lat = Math.Clamp(lat, -90m, 90m);

        var longitudeStep = (double)LatitudeStep / Math.Max(Math.Cos((double)lat * Math.PI / 180), MinCosine);
        var lng = Math.Round(
            Math.Round((double)longitude / longitudeStep, MidpointRounding.AwayFromZero) * longitudeStep,
            6,
            MidpointRounding.AwayFromZero);
        return (lat, Math.Clamp(decimal.Round((decimal)lng, 6, MidpointRounding.AwayFromZero), -180m, 180m));
    }
}

internal static class ScreeningText
{
    public const int LabelsMaxLength = 200;

    public static string? Labels(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return null;
        }

        var joined = string.Join(",", labels);
        return joined.Length <= LabelsMaxLength ? joined : joined[..LabelsMaxLength];
    }
}

/// <summary>
/// The parts of a post its author writes and can later edit. <c>Types</c> are every type of bad driving the author
/// chose (the first is the main one); when null or empty the post has just <c>Category</c>.
/// </summary>
public sealed record PostDetails(
    string Title,
    string? Description,
    IncidentCategory Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    IReadOnlyList<IncidentCategory>? Types = null,
    VehicleDetails? Vehicle = null,
    bool? ReportedToPolice = null);

/// <summary>
/// The vehicle driven badly: all optional, but the type is needed once anything else is given. Make and model come
/// from <see cref="VehicleCatalogue"/>; never the registration.
/// </summary>
public sealed record VehicleDetails(VehicleType? Type, string? Make = null, string? Model = null, VehicleColour? Colour = null)
{
    public static readonly VehicleDetails None = new(Type: null);
}

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
