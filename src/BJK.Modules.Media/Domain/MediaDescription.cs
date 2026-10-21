namespace BJK.Modules.Media.Domain;

/// <summary>Where a "Suggest a description" request is.</summary>
public enum DescriptionStatus
{
    /// <summary>Waiting for the media worker.</summary>
    Queued,

    /// <summary>A draft is ready.</summary>
    Ready,

    /// <summary>No draft: the model failed, or its text didn't pass the checks.</summary>
    Failed,
}

/// <summary>
/// One "Suggest a description" request (table <c>media.Descriptions</c>): the media it's for, what the person had
/// filled in so far, and the draft title and description. Only the draft text is kept, never the pictures sent to the
/// model. Deleted with the account. The person can use, edit or ignore the draft; nothing is published from here.
/// </summary>
public sealed class MediaDescription
{
    public const int TitleMaxLength = 120;
    public const int TextMaxLength = 1000;
    public const int MediaIdsMaxLength = 400;
    public const int ContextMaxLength = 2000;
    public const int ModelMaxLength = 100;
    public const int FailureReasonMaxLength = 300;

    private MediaDescription()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The media, comma-separated, in the order the person chose them.</summary>
    public string MediaIds { get; private set; } = string.Empty;

    /// <summary>What the person had filled in (types, place, time, vehicle), as JSON, for the model.</summary>
    public string Context { get; private set; } = "{}";

    public DescriptionStatus Status { get; private set; }

    public string? Title { get; private set; }

    public string? Text { get; private set; }

    /// <summary>The model that wrote it, e.g. "qwen3-vl:8b" or "Development".</summary>
    public string? Model { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The media ids as a list.</summary>
    public IReadOnlyList<Guid> MediaIdList =>
        MediaIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    public static MediaDescription Create(Guid userId, IReadOnlyList<Guid> mediaIds, string context, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        MediaIds = string.Join(',', mediaIds),
        Context = context.Length > ContextMaxLength ? "{}" : context,
        Status = DescriptionStatus.Queued,
        CreatedAt = now,
    };

    public void MarkReady(string title, string text, string model, DateTimeOffset now)
    {
        Status = DescriptionStatus.Ready;
        Title = Truncate(title, TitleMaxLength);
        Text = Truncate(text, TextMaxLength);
        Model = Truncate(model, ModelMaxLength);
        CompletedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status != DescriptionStatus.Queued)
        {
            return;
        }

        Status = DescriptionStatus.Failed;
        FailureReason = Truncate(reason, FailureReasonMaxLength);
        CompletedAt = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
