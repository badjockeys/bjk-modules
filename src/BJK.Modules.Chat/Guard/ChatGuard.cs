using System.Text.RegularExpressions;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.UseCases;
using BJK.Modules.Safety;

namespace BJK.Modules.Chat.Guard;

/// <summary>Why a question was stopped before it reached the agent.</summary>
public enum GuardReason
{
    /// <summary>Allowed through.</summary>
    None,

    /// <summary>"Hi", "thanks" and the like: a friendly reply, no strike, no allowance used.</summary>
    Greeting,

    /// <summary>A number plate, phone number, email address, street address or social handle.</summary>
    PersonalDetails,

    /// <summary>Asks about specific people, members or accounts ("who owns...", "which member...").</summary>
    People,

    /// <summary>Tries to change the assistant's instructions, see its prompt or schema, or send SQL.</summary>
    PromptTrick,

    /// <summary>Contains a web address.</summary>
    WebAddress,

    /// <summary>Abusive, threatening or hateful (the content screener would hold it).</summary>
    Abuse,

    /// <summary>Not about bad driving or parking reported on Bad Jockeys (the topic gate said no).</summary>
    OffTopic,
}

/// <summary>The guard's decision: <see cref="Reply"/> is set when the question stops here.</summary>
public sealed record GuardVerdict(GuardReason Reason, ChatAnswerDto? Reply)
{
    public static readonly GuardVerdict Allowed = new(GuardReason.None, null);

    /// <summary>True for misuse, which counts as a strike. Greetings don't.</summary>
    public bool IsStrike => Reason is not (GuardReason.None or GuardReason.Greeting);
}

/// <summary>
/// The guard rails around Ask Bad Jockeys, enforced in code rather than left to the model's prompt. A question has
/// to pass, in order:
/// <list type="number">
/// <item>the fixed checks: personal details (the Safety module's text checks: plates, phone numbers, emails,
/// addresses, handles), web addresses, questions about specific people, and prompt or SQL tricks;</item>
/// <item>the content screener (abuse, threats, slurs);</item>
/// <item>the <see cref="ITopicGate"/>: a short, separate model call that only answers whether the question is about
/// bad driving or parking reported on Bad Jockeys.</item>
/// </list>
/// Only then does the agent see it. The agent's reply is checked again (<see cref="CheckAnswerAsync"/>).
/// </summary>
public sealed partial class ChatGuard(IContentScreener screener, ITopicGate topics)
{
    internal const string PeopleMessage =
        "I can't look up people, accounts or number plates. I can tell you how many reports there are on a road or in a town, and when.";

    internal const string TrickMessage =
        "I can't help with that. I only answer questions about bad driving and parking reported on Bad Jockeys.";

    internal const string WebAddressMessage =
        "Links can't be used here. Ask me about bad driving or parking reported on Bad Jockeys.";

    internal const string AbuseMessage =
        "Let's keep it civil. Ask me about bad driving or parking reported on Bad Jockeys.";

    internal const string OffTopicMessage =
        "I only answer questions about bad driving and parking reported on Bad Jockeys, like counts, trends, places and times.";

    internal const string GreetingMessage =
        "Hi! Ask me about bad driving or parking reported on Bad Jockeys: how many, where, when, and whether it's getting worse.";

    /// <summary>
    /// Checks a question. <paramref name="previousQuestion"/> is the last verified turn's, for follow-ups;
    /// <paramref name="examples"/> are the member's example questions (<see cref="ChatExamples.For"/>), offered as
    /// suggestions when a question stops here.
    /// </summary>
    public async Task<GuardVerdict> CheckQuestionAsync(
        string question,
        string? previousQuestion,
        IReadOnlyList<string> examples,
        CancellationToken cancellationToken)
    {
        if (Fixed(question, examples) is { } stopped)
        {
            return stopped;
        }

        var screening = await screener.ScreenAsync(question, cancellationToken);
        if (screening.Verdict == ScreeningVerdict.Hold)
        {
            return Stop(GuardReason.Abuse, AbuseMessage, ChatAnswerKind.Refused, examples);
        }

        if (!await topics.IsOnTopicAsync(question, previousQuestion, cancellationToken))
        {
            return Stop(GuardReason.OffTopic, OffTopicMessage, ChatAnswerKind.OutOfScope, examples);
        }

        return GuardVerdict.Allowed;
    }

    /// <summary>
    /// True when the agent's reply is fit to show: no personal details and nothing the screener would hold. (SQL in
    /// the text is already sent back to the model by the agent.)
    /// </summary>
    public async Task<bool> CheckAnswerAsync(ChatAnswerDto answer, CancellationToken cancellationToken)
    {
        if (TextSafety.Check(answer.Text) is not null || TextSafety.ContainsWebAddress(answer.Text) || ChatAgent.LooksLikeSql(answer.Text))
        {
            return false;
        }

        return (await screener.ScreenAsync(answer.Text, cancellationToken)).Verdict != ScreeningVerdict.Hold;
    }

    /// <summary>The checks that need no model or service: greetings, personal details, links, people, tricks.</summary>
    internal static GuardVerdict? Fixed(string question, IReadOnlyList<string>? examples = null)
    {
        examples ??= ChatExamples.Untailored;
        if (Greeting().IsMatch(question))
        {
            return new GuardVerdict(GuardReason.Greeting, new ChatAnswerDto(GreetingMessage, ChatAnswerKind.Clarify, null, null, [.. examples.Take(3)]));
        }

        // Personal details first: an email address also contains a web domain.
        if (TextSafety.Check(question) is not null)
        {
            return Stop(GuardReason.PersonalDetails, PeopleMessage, ChatAnswerKind.Refused, examples);
        }

        if (TextSafety.ContainsWebAddress(question))
        {
            return Stop(GuardReason.WebAddress, WebAddressMessage, ChatAnswerKind.Refused, examples);
        }

        if (AboutPeople().IsMatch(question))
        {
            return Stop(GuardReason.People, PeopleMessage, ChatAnswerKind.Refused, examples);
        }

        if (InstructionTrick().IsMatch(question) || SqlTrick().IsMatch(question) || InternalsTrick().IsMatch(question))
        {
            return Stop(GuardReason.PromptTrick, TrickMessage, ChatAnswerKind.Refused, examples);
        }

        return null;
    }

    private static GuardVerdict Stop(GuardReason reason, string message, ChatAnswerKind kind, IReadOnlyList<string> examples) =>
        new(reason, new ChatAnswerDto(message, kind, null, null, [.. examples.Take(2)]));

    [GeneratedRegex(@"^\s*(hi|hello|hey|hiya|yo|thanks|thank you|thank u|ta|cheers|ok|okay|good (morning|afternoon|evening))\b[\s,!.]*(there|again|mate|so much|a lot)?[\s,!.:)]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    // Specific people, members or accounts. "Who's at fault" and "which people are most at risk" stay allowed.
    [GeneratedRegex(
        @"\bwho\s+(owns|owned|is\s+driving|was\s+driving|drives|drove|posted|uploaded|reported|filmed|is\s+the\s+(driver|owner|poster)|was\s+the\s+(driver|owner|poster))\b"
        + @"|\b(which|what)\s+(members?|users?|accounts?|posters?|usernames?|handles?)\b"
        + @"|\b(names?|address(es)?|identit(y|ies)|phone\s+numbers?|details|whereabouts)\s+of\s+(the|that|this|a|an|those|these)\s+(drivers?|members?|users?|posters?|persons?|people|owners?|cars?|vans?)\b"
        + @"|\btrack\s+(down|them|him|her)\b|\bwhere\s+(does|do|did)\s+(he|she|they|the\s+driver)\s+live\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AboutPeople();

    [GeneratedRegex(
        @"\b(ignore|disregard|forget|override|bypass)\b.{0,40}\b(instructions?|rules|prompts?|guidelines|guard\s*rails|restrictions|above|previous|everything)\b"
        + @"|\b(system|developer|hidden)\s+(prompt|message|instructions?|mode)\b"
        + @"|\b(jailbreak|jail\s+break|dan\s+mode|do\s+anything\s+now)\b"
        + @"|\byou\s+are\s+(now|no\s+longer)\b|\bpretend\s+(to\s+be|you\s+are|you're)\b|\bact\s+as\b|\brole\s*-?\s*play\b"
        + @"|\byour\s+(instructions|prompt|rules|guidelines|system\s+message|configuration|settings)\b|\bthe\s+(system\s+message|prompt)\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InstructionTrick();

    [GeneratedRegex(
        @"\bselect\s+(\*|top\s+\d|distinct\b|count\s*\()|\bfrom\s+(reporting|dbo|sys|posts|users|auth|media|chat|profiles)\.\w|\binsert\s+into\b|\bdelete\s+from\b|\bdrop\s+(table|database|view|schema|login|user)\b"
        + @"|\balter\s+(table|database|login|user)\b|\btruncate\s+table\b|\bexec(ute)?\s*[(\s]+(sp_|xp_|@)|\bxp_\w+|\bsp_\w+"
        + @"|;\s*--|/\*|\bunion\s+(all\s+)?select\b|\bwaitfor\s+delay\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SqlTrick();

    [GeneratedRegex(
        @"\b(show|tell|give|print|list|reveal|display|dump|what('?s|\s+is|\s+are))\b.{0,30}\b(sql|quer(y|ies)|schema|database|tables|columns|connection\s+strings?|passwords?|api\s+keys?|prompt|source\s+code)\b"
        + @"|\binformation_schema\b|\bsys\.\w+|\breporting\.\w+|\bbjk_\w+|\b(posts|users|auth|media|chat|profiles|notifications|dbo|idsrv)\.[A-Z]\w*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InternalsTrick();
}
