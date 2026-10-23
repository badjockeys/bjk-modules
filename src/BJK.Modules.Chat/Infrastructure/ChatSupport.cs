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
            "Ask Bad Jockeys ({Plan}) answered {Kind} to {Question} after {ModelCalls} model calls in {Milliseconds} ms. Steps: {Steps}");

    public static readonly Action<ILogger, string, Exception?> ModelUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(ModelUnavailableEventId, "ChatModelUnavailable"),
            "Ask Bad Jockeys couldn't answer: {Reason}. The question was given back.");

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
    public string Section => "askBadJockeys";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var today = UkDay.Today(clock.GetUtcNow());
        var used = await usage.UsedAsync(userId, today, cancellationToken);
        return new
        {
            questionsAskedToday = used,
            note = "Chats aren't saved: questions and answers stay in the chat window until it's closed. Only today's count is kept, for the daily limit.",
        };
    }
}
