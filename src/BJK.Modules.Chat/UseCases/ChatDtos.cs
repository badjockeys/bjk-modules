namespace BJK.Modules.Chat.UseCases;

/// <summary>What sort of reply this is, so the app can style it.</summary>
public enum ChatAnswerKind
{
    /// <summary>An answer from the figures, usually with a card.</summary>
    Answer,

    /// <summary>Too few reports to show (fewer than 3); try a wider area or a longer period.</summary>
    TooFew,

    /// <summary>A question back to the member, e.g. which town "near me" means.</summary>
    Clarify,

    /// <summary>Something it won't do, such as identify a person or number plate.</summary>
    Refused,

    /// <summary>Not about reported driving or parking.</summary>
    OutOfScope,

    /// <summary>It couldn't work the question out.</summary>
    Failed,
}

/// <summary>How to draw the card under the answer.</summary>
public enum ChatCardType
{
    /// <summary>One figure.</summary>
    Number,

    /// <summary>A trend over time.</summary>
    Line,

    /// <summary>A comparison.</summary>
    Bar,

    /// <summary>Towns, roads or hot spots, with a mini map where the points have coordinates.</summary>
    Places,

    /// <summary>Several figures per row.</summary>
    Table,
}

/// <summary>One bar, point or place on a card.</summary>
/// <param name="Label">The label: a month, hour, kind of incident, place...</param>
/// <param name="Value">The figure, or null when it's hidden.</param>
/// <param name="Hidden">True when there were fewer than 3 incidents, so the figure isn't shown ("fewer than 3").</param>
/// <param name="Latitude">For places: the area's latitude (about 1 km squares), when known.</param>
/// <param name="Longitude">For places: the area's longitude, when known.</param>
public sealed record ChatCardPointDto(string Label, double? Value, bool Hidden, double? Latitude = null, double? Longitude = null);

/// <summary>
/// A card drawn from the query's rows (never from figures the model wrote). <see cref="Points"/> for number, line,
/// bar and places cards; <see cref="Columns"/> and <see cref="Rows"/> for tables ("&lt;3" where hidden).
/// </summary>
public sealed record ChatCardDto(
    ChatCardType Type,
    string? Title,
    string? ValueLabel,
    IReadOnlyList<ChatCardPointDto> Points,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<string?>>? Rows,
    int HiddenRows);

/// <summary>"Open on the map": where to centre the heat map and which subcategories to filter by.</summary>
/// <param name="Latitude">Centre latitude, if the answer was about an area.</param>
/// <param name="Longitude">Centre longitude.</param>
/// <param name="Zoom">Suggested zoom.</param>
/// <param name="Subcategories">Up to 3 subcategory names (as in the heat map's categories filter).</param>
public sealed record ChatMapLinkDto(double? Latitude, double? Longitude, int? Zoom, IReadOnlyList<string> Subcategories);

/// <summary>One answer: a sentence or two, maybe a card, maybe a map link, and follow-up suggestions.</summary>
public sealed record ChatAnswerDto(
    string Text,
    ChatAnswerKind Kind,
    ChatCardDto? Card,
    ChatMapLinkDto? Map,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// The caller's allowance. Free members (and logged-out callers) get <see cref="CanAsk"/> false and the example
/// questions for the locked preview.
/// </summary>
/// <param name="Plan"><c>free</c>, <c>premium</c> or <c>premium_plus</c>; null when logged out.</param>
/// <param name="CanAsk">True for Premium and Premium +.</param>
/// <param name="Limit">Questions per UK day (0 for Free).</param>
/// <param name="Used">Asked today.</param>
/// <param name="Remaining">Left today.</param>
/// <param name="ResetsAt">When the allowance starts again (midnight UK time).</param>
/// <param name="HistoryFrom">The earliest date the plan can ask about (null for Free).</param>
/// <param name="ExampleQuestions">Questions to show in the empty chat or the locked preview.</param>
/// <param name="Paused">
/// True when the member has reached the day's misuse limit (too many blocked questions): asking returns 429
/// <c>chat_paused</c> until <see cref="ResetsAt"/>.
/// </param>
public sealed record ChatUsageDto(
    string? Plan,
    bool CanAsk,
    int Limit,
    int Used,
    int Remaining,
    DateTimeOffset ResetsAt,
    DateOnly? HistoryFrom,
    IReadOnlyList<string> ExampleQuestions,
    bool Paused = false);

/// <summary>The answer to a question plus the allowance after it.</summary>
/// <param name="Answer">The answer.</param>
/// <param name="Usage">The allowance after it.</param>
/// <param name="TurnToken">Send it back with this turn in <c>history</c>; turns without a valid token are ignored. Null for blocked questions, which aren't part of the history.</param>
public sealed record ChatReplyDto(ChatAnswerDto Answer, ChatUsageDto Usage, string? TurnToken = null);
