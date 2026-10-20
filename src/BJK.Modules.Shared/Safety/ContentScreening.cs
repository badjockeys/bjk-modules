using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Shared.Safety;

/// <summary>What happens to screened text.</summary>
public enum ScreeningVerdict
{
    /// <summary>Publish as normal.</summary>
    Allow,

    /// <summary>Publish, but flag for a moderator to look at.</summary>
    Flag,

    /// <summary>Hold for review: only the author sees it until a moderator approves it.</summary>
    Hold,
}

/// <summary>
/// The result of screening a piece of text. <see cref="Score"/> runs from 0 (harmless) to 1 (certainly abusive);
/// <see cref="Labels"/> say why, using the classifier's category names (threat, insult, identity_attack...).
/// </summary>
public sealed record ScreeningResult(double Score, IReadOnlyList<string> Labels, ScreeningVerdict Verdict)
{
    public static readonly ScreeningResult Clean = new(0, [], ScreeningVerdict.Allow);
}

/// <summary>
/// Scores comments and post text for abuse, threats, slurs and calls to confront people. High scores are held for
/// review; medium scores publish but are flagged. Chosen by <c>Screening:Provider</c>.
/// </summary>
public interface IContentScreener
{
    Task<ScreeningResult> ScreenAsync(string text, CancellationToken cancellationToken);
}

/// <summary>Content screening settings. Section <c>Screening</c>.</summary>
public sealed class ScreeningOptions
{
    public const string SectionName = "Screening";

    public const string KeywordsProvider = "Keywords";
    public const string HttpProvider = "Http";

    /// <summary>
    /// <c>Keywords</c> (default): the built-in keyword and phrase list only. <c>Http</c>: the keyword list plus a
    /// classifier service at <see cref="Endpoint"/> (such as an open-source Detoxify model in a sidecar container);
    /// the higher score wins.
    /// </summary>
    public string Provider { get; set; } = KeywordsProvider;

    /// <summary>
    /// The classifier, for the <c>Http</c> provider. It receives <c>POST {"text": "..."}</c> and answers
    /// <c>{"scores": {"toxicity": 0.97, "threat": 0.12, ...}}</c> with scores from 0 to 1.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Scores at or above this publish but are flagged. Default 0.5.</summary>
    public double FlagThreshold { get; set; } = 0.5;

    /// <summary>Scores at or above this are held for review. Default 0.8.</summary>
    public double HoldThreshold { get; set; } = 0.8;

    /// <summary>How long to wait for the classifier before falling back to the keyword list. Default 3 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    public ScreeningVerdict VerdictFor(double score) =>
        score >= HoldThreshold ? ScreeningVerdict.Hold
        : score >= FlagThreshold ? ScreeningVerdict.Flag
        : ScreeningVerdict.Allow;
}

internal sealed class ScreeningOptionsValidator : IValidateOptions<ScreeningOptions>
{
    public ValidateOptionsResult Validate(string? name, ScreeningOptions options)
    {
        var failures = new List<string>();
        if (options.Provider is not (ScreeningOptions.KeywordsProvider or ScreeningOptions.HttpProvider))
        {
            failures.Add($"Screening:Provider must be '{ScreeningOptions.KeywordsProvider}' or '{ScreeningOptions.HttpProvider}'.");
        }

        if (options.Provider == ScreeningOptions.HttpProvider && options.Endpoint is not { IsAbsoluteUri: true })
        {
            failures.Add("Screening:Endpoint must be an absolute URL when Screening:Provider is Http.");
        }

        if (options.FlagThreshold is <= 0 or > 1 || options.HoldThreshold is <= 0 or > 1 || options.FlagThreshold > options.HoldThreshold)
        {
            failures.Add("Screening:FlagThreshold and Screening:HoldThreshold must be between 0 and 1, with FlagThreshold no higher than HoldThreshold.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            failures.Add("Screening:Timeout must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// The built-in screener: a list of words and phrases (threats, slurs, abuse and calls to find or confront someone),
/// each with a severity. Text is lower-cased, common character swaps are undone ("k1ll" → "kill") and repeated
/// letters are collapsed ("kiiiill" → "kill") before matching whole words and phrases.
/// </summary>
public sealed class KeywordContentScreener(IOptions<ScreeningOptions> options) : IContentScreener
{
    public Task<ScreeningResult> ScreenAsync(string text, CancellationToken cancellationToken) =>
        Task.FromResult(Screen(text));

    /// <summary>Screens text synchronously.</summary>
    public ScreeningResult Screen(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ScreeningResult.Clean;
        }

        var normalised = ScreeningKeywords.Normalise(text);
        var matches = ScreeningKeywords.All
            .Where(k => normalised.Contains(k.Pattern, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            return ScreeningResult.Clean;
        }

        var score = matches.Max(m => m.Score);
        var labels = matches.Select(m => m.Label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new ScreeningResult(score, labels, options.Value.VerdictFor(score));
    }
}

/// <summary>
/// The keyword screener plus a classifier service (<see cref="ScreeningOptions.Endpoint"/>). The higher score wins.
/// If the classifier can't be reached the keyword result is used, so posting never breaks because the classifier is down.
/// </summary>
internal sealed class HttpContentScreener(
    HttpClient http,
    KeywordContentScreener keywords,
    IOptions<ScreeningOptions> options,
    ILogger<HttpContentScreener> logger)
    : IContentScreener
{
    public const string ClassifierUnavailableLabel = "classifier_unavailable";

    public async Task<ScreeningResult> ScreenAsync(string text, CancellationToken cancellationToken)
    {
        var keywordResult = keywords.Screen(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return keywordResult;
        }

        var settings = options.Value;
        try
        {
            using var response = await http.PostAsJsonAsync(settings.Endpoint, new ClassifierRequest(text), cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ClassifierResponse>(cancellationToken);
            var scores = body?.Scores ?? new Dictionary<string, double>();

            var classifierScore = scores.Count == 0 ? 0 : Math.Clamp(scores.Values.Max(), 0, 1);
            var score = Math.Max(classifierScore, keywordResult.Score);
            var labels = scores
                .Where(s => s.Value >= settings.FlagThreshold)
                .Select(s => s.Key)
                .Concat(keywordResult.Labels)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            return new ScreeningResult(score, labels, settings.VerdictFor(score));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "The screening classifier couldn't be reached; using the keyword list only.");
            return keywordResult with { Labels = [.. keywordResult.Labels, ClassifierUnavailableLabel] };
        }
    }

    private sealed record ClassifierRequest(string Text);

    private sealed record ClassifierResponse(Dictionary<string, double>? Scores);
}

/// <summary>The built-in word and phrase list. Reviewed by the trust and safety lead; extend it here, in code.</summary>
public static class ScreeningKeywords
{
    /// <summary>Scores for each severity. With the default thresholds, High is held, Medium is flagged, Low is allowed.</summary>
    public const double High = 0.95;
    public const double Medium = 0.65;
    public const double Low = 0.3;

    public const string Threat = "threat";
    public const string Confrontation = "confrontation";
    public const string IdentityAttack = "identity_attack";
    public const string Insult = "insult";

    private static readonly (string Phrase, string Label, double Score)[] Entries =
    [
        // Threats of violence.
        ("kill you", Threat, High), ("kill him", Threat, High), ("kill her", Threat, High), ("kill them", Threat, High),
        ("i will kill", Threat, High), ("gonna kill", Threat, High), ("going to kill", Threat, High),
        ("should be killed", Threat, High), ("deserves to die", Threat, High), ("hope he dies", Threat, High),
        ("hope she dies", Threat, High), ("hope you die", Threat, High), ("shoot him", Threat, High),
        ("stab him", Threat, High), ("stab you", Threat, High), ("burn his house", Threat, High),
        ("burn her house", Threat, High), ("smash his face", Threat, High), ("smash her face", Threat, High),
        ("kick his head in", Threat, High), ("kick your head in", Threat, High), ("batter him", Threat, High),
        ("beat him up", Threat, High), ("deserves a beating", Threat, High), ("deserves a kicking", Threat, High),
        ("break his legs", Threat, High), ("break your legs", Threat, High), ("ram him off the road", Threat, High),

        // Finding, following or confronting the people in a clip.
        ("i know where you live", Confrontation, High), ("i know where he lives", Confrontation, High),
        ("we know where you live", Confrontation, High), ("find out where he lives", Confrontation, High),
        ("find out where she lives", Confrontation, High), ("where does he live", Confrontation, High),
        ("where does she live", Confrontation, High), ("know where he works", Confrontation, High),
        ("track him down", Confrontation, High), ("track her down", Confrontation, High),
        ("hunt him down", Confrontation, High), ("hunt her down", Confrontation, High),
        ("pay him a visit", Confrontation, High), ("pay her a visit", Confrontation, High),
        ("go round his house", Confrontation, High), ("go round to his house", Confrontation, High),
        ("wait for him", Confrontation, High), ("teach him a lesson", Confrontation, High),
        ("sort him out", Confrontation, High), ("get his address", Confrontation, High),
        ("post his address", Confrontation, High), ("dox", Confrontation, High), ("doxx", Confrontation, High),
        ("anyone know who this is", Confrontation, Medium), ("does anyone know him", Confrontation, Medium),
        ("does anyone know her", Confrontation, Medium), ("who is this guy", Confrontation, Medium),
        ("name and shame", Confrontation, Medium), ("find this guy", Confrontation, Medium),
        ("find this driver", Confrontation, Medium), ("find him", Confrontation, Medium),

        // Slurs: identity-based abuse is always held.
        ("nigger", IdentityAttack, High), ("nigga", IdentityAttack, High), ("paki", IdentityAttack, High),
        ("faggot", IdentityAttack, High), ("fag", IdentityAttack, High), ("tranny", IdentityAttack, High),
        ("chink", IdentityAttack, High), ("spic", IdentityAttack, High), ("kike", IdentityAttack, High),
        ("wog", IdentityAttack, High), ("gyppo", IdentityAttack, High), ("pikey", IdentityAttack, High),
        ("raghead", IdentityAttack, High), ("retard", IdentityAttack, Medium), ("spastic", IdentityAttack, Medium),

        // Abuse aimed at someone: flagged. Milder insults are recorded but allowed (bad driving makes people cross).
        ("cunt", Insult, Medium), ("wanker", Insult, Medium), ("twat", Insult, Medium), ("dickhead", Insult, Medium),
        ("bellend", Insult, Medium), ("knobhead", Insult, Medium), ("scumbag", Insult, Medium),
        ("piece of shit", Insult, Medium), ("prick", Insult, Low), ("idiot", Insult, Low), ("moron", Insult, Low),
        ("muppet", Insult, Low), ("tosser", Insult, Low),
    ];

    /// <summary>
    /// Whether a hashtag (letters, digits and underscores, no spaces) is offensive: it is, starts or ends with any
    /// threat, slur or flagged insult on the list (spaces removed), e.g. <c>killhim</c>; words of four letters or fewer
    /// only when they're the whole tag. Words inside a tag don't count, so place names such as Scunthorpe are fine.
    /// Low-severity insults (idiot, muppet) are allowed. Moderators can hide anything this misses from Trending.
    /// </summary>
    /// <param name="tag">The tag, without the #.</param>
    public static bool IsOffensiveTag(string tag)
    {
        var squashed = Normalise(tag).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (squashed.Length == 0)
        {
            return false;
        }

        foreach (var entry in Entries)
        {
            if (entry.Score < Medium)
            {
                continue;
            }

            var phrase = Normalise(entry.Phrase).Replace(" ", string.Empty, StringComparison.Ordinal);
            // Short words only count as the whole tag: "spic" and "paki" start everyday words (spicy, Pakistan).
            var matches = phrase.Length <= 4
                ? squashed == phrase
                : squashed.StartsWith(phrase, StringComparison.Ordinal) || squashed.EndsWith(phrase, StringComparison.Ordinal);
            if (phrase.Length > 0 && matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every entry, with its pattern normalised the same way as screened text.</summary>
    internal static readonly IReadOnlyList<Keyword> All =
        Entries.Select(e => new Keyword(Normalise(e.Phrase), e.Label, e.Score)).ToList();

    /// <summary>
    /// Lower-cases, undoes common character swaps, turns everything that isn't a letter into a space, collapses
    /// repeated letters and spaces, and pads with spaces so whole words can be matched with " word ".
    /// </summary>
    public static string Normalise(string text)
    {
        var result = new StringBuilder(text.Length + 2);
        result.Append(' ');
        var previous = ' ';
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' => 't',
                _ => raw,
            };

            if (c is < 'a' or > 'z')
            {
                c = ' ';
            }

            if (c != previous)
            {
                result.Append(c);
                previous = c;
            }
        }

        if (previous != ' ')
        {
            result.Append(' ');
        }

        return result.ToString();
    }

    internal sealed record Keyword(string Pattern, string Label, double Score);
}
