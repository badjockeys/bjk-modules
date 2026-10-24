using System.Text.Json;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Shared.PersonalData;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Users;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Chat.Infrastructure;

/// <summary>UK calendar days (Europe/London, following British Summer Time), for the daily question limits.</summary>
internal static class UkDay
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        throw new InvalidOperationException("The Europe/London time zone isn't available (install tzdata in the container).");
    });

    /// <summary>The UK date at an instant.</summary>
    public static DateOnly Today(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime);

    /// <summary>Midnight UK time at the start of the next UK day: when the limits reset.</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset instant)
    {
        var local = Today(instant).AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Midnight always exists in the UK (the clocks change at 1am and 2am), so there's exactly one offset.
        return new DateTimeOffset(local, Zone.Value.GetUtcOffset(local));
    }
}

/// <summary>
/// The review log: every question with the SQL the model tried and how each went, so we can improve the column
/// descriptions and worked examples. No member id or other account details; the plan only.
/// </summary>
internal static class ChatLog
{
    /// <summary>Event id of <see cref="Answered"/>, to find the questions in Kibana.</summary>
    public const int AnsweredEventId = 4901;

    /// <summary>Event id of <see cref="ModelUnavailable"/>, to alert on.</summary>
    public const int ModelUnavailableEventId = 4902;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, string, string, int, long, string, Exception?> AnsweredMessage =
        LoggerMessage.Define<string, string, string, int, long, string>(
            LogLevel.Information,
            new EventId(AnsweredEventId, "ChatQuestionAnswered"),
            "Ask AI ({Plan}) answered {Kind} to {Question} after {ModelCalls} model calls in {Milliseconds} ms. Steps: {Steps}");

    /// <summary>Event id of <see cref="NotSwitchedOn"/>: a question asked while no model is set (the placeholder).</summary>
    public const int NotSwitchedOnEventId = 4905;

    private static readonly Action<ILogger, string, Exception?> NotSwitchedOnMessage =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(NotSwitchedOnEventId, "ChatNotSwitchedOn"),
            "Ask AI ({Plan}) was asked a question, but no model is set (Chat:Model:Endpoint is empty), so it wasn't sent anywhere.");

    /// <summary>The placeholder's log line: the plan only, no member id or question.</summary>
    public static void NotSwitchedOn(ILogger logger, SubscriptionPlan plan) =>
        NotSwitchedOnMessage(logger, SubscriptionPlanClaims.ToClaimValue(plan), null);

    /// <summary>Event id of <see cref="Blocked"/>: a question stopped by the guard rails (a strike).</summary>
    public const int BlockedEventId = 4903;

    private static readonly Action<ILogger, string, string, string, Exception?> BlockedMessage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(BlockedEventId, "ChatQuestionBlocked"),
            "Ask AI ({Plan}) blocked a question ({Reason}): {Question}");

    /// <summary>A blocked question, with the reason. No member id; questions with personal details aren't logged.</summary>
    public static void Blocked(ILogger logger, SubscriptionPlan plan, string reason, string question) =>
        BlockedMessage(logger, SubscriptionPlanClaims.ToClaimValue(plan), reason, question, null);

    public static readonly Action<ILogger, string, Exception?> ModelUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(ModelUnavailableEventId, "ChatModelUnavailable"),
            "Ask AI couldn't answer: {Reason}. The question was given back.");

    public static void Answered(ILogger logger, SubscriptionPlan plan, string question, AgentResult result, TimeSpan duration) =>
        AnsweredMessage(
            logger,
            SubscriptionPlanClaims.ToClaimValue(plan),
            result.Answer.Kind.ToString(),
            question,
            result.ModelCalls,
            (long)duration.TotalMilliseconds,
            JsonSerializer.Serialize(result.Steps, Json),
            null);
}

/// <summary>Deletes a member's question counts with their account.</summary>
internal sealed class ChatUserLifecycleHandler(IChatUsage usage) : IUserLifecycleHandler
{
    public Task HandleAsync(UserLifecycleEvent userEvent, CancellationToken cancellationToken) =>
        userEvent.Type == UserLifecycleEventType.Deleted
            ? usage.DeleteForUserAsync(userEvent.UserId, cancellationToken)
            : Task.CompletedTask;
}

/// <summary>Adds today's question count to a member's data export (nothing else about the chat is kept).</summary>
internal sealed class ChatPersonalDataExporter(IChatUsage usage, TimeProvider clock) : IPersonalDataExporter
{
    public string Section => "askAi";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var today = UkDay.Today(clock.GetUtcNow());
        var counts = await usage.GetAsync(userId, today, cancellationToken);
        return new
        {
            questionsAskedToday = counts.Questions,
            questionsBlockedToday = counts.Strikes,
            note = "Chats aren't saved: questions and answers stay in the chat window until it's closed. Only today's counts are kept, for the daily limit and the misuse limit.",
        };
    }
}
