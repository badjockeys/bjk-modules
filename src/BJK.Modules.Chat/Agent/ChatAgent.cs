using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Chat.UseCases;

namespace BJK.Modules.Chat.Agent;

/// <summary>One earlier turn of this chat window, sent back by the app (history lives only in the window).</summary>
/// <param name="Question">What the member asked.</param>
/// <param name="Answer">The answer's text.</param>
public sealed record ChatTurn(string Question, string Answer);

/// <summary>A query the agent tried, for the review log: the SQL as the model wrote it and how it went.</summary>
public sealed record AgentStep(string Sql, string? Problem, int Rows, int HiddenRows, long Milliseconds);

/// <summary>What the agent came back with, plus the steps it took (logged, never shown to members).</summary>
public sealed record AgentResult(ChatAnswerDto Answer, IReadOnlyList<AgentStep> Steps, int ModelCalls);

/// <summary>
/// Ask Bad Jockeys' loop: give the model the question and the tools, run the SQL it writes through
/// <see cref="SqlGuard"/> and <see cref="IIncidentQueries"/>, feed the results back, and stop when it calls
/// <c>answer</c> (or after <see cref="ChatOptions.MaxSteps"/> model calls). Cards are drawn from the last query's
/// rows, never from figures the model writes, so a chart can't show a number the data doesn't have.
/// </summary>
public sealed partial class ChatAgent(IChatModel model, IIncidentQueries queries)
{
    /// <summary>Rows the model sees from one query; the card can use all of them.</summary>
    internal const int RowsShownToModel = 50;

    /// <summary>Earlier turns sent to the model, newest last.</summary>
    internal const int HistoryTurns = 6;

    internal static readonly ChatAnswerDto Fallback = new(
        "Sorry, I couldn't work that one out. Try asking it another way, for example \"How many close passes in Leeds this year?\"",
        ChatAnswerKind.Failed, null, null, []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Plain JSON for the model: "&lt;3" and accented place names as they are, not \u003C3.</summary>
    private static readonly JsonSerializerOptions ModelJson = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<AgentResult> AskAsync(string question, IReadOnlyList<ChatTurn> history, AgentContext context, CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage> { ModelMessage.System(AgentPrompt.System(context)) };
        foreach (var turn in history.TakeLast(HistoryTurns))
        {
            messages.Add(ModelMessage.User(Clip(turn.Question, ChatOptions.MaxQuestionLength)));
            messages.Add(ModelMessage.Assistant(Clip(turn.Answer, 1000)));
        }

        messages.Add(ModelMessage.User(question));

        var steps = new List<AgentStep>();
        QueryResult? last = null;
        var nudged = false;
        for (var call = 1; call <= ChatOptions.MaxSteps; call++)
        {
            var reply = await model.CompleteAsync(messages, AgentPrompt.Tools, cancellationToken);
            messages.Add(reply);

            if (reply.ToolCalls is not { Count: > 0 } calls)
            {
                // Plain text instead of the answer tool. Fine after a query ran; otherwise any figures would be made up.
                if (reply.Content is { } text && (last is not null || (nudged && !HasDigits(text))) && !LooksLikeSql(text))
                {
                    return new AgentResult(new ChatAnswerDto(text, last is null ? ChatAnswerKind.OutOfScope : ChatAnswerKind.Answer, null, null, []), steps, call);
                }

                if (nudged)
                {
                    return new AgentResult(Fallback, steps, call);
                }

                nudged = true;
                messages.Add(ModelMessage.User("Use the tools: call run_sql to look the figures up, then answer. Or call answer with kind refuse, clarify or out_of_scope."));
                continue;
            }

            var ranQueryNow = calls.Any(c => c.Name == AgentPrompt.RunSql);
            foreach (var toolCall in calls)
            {
                switch (toolCall.Name)
                {
                    case AgentPrompt.RunSql:
                        var (content, result, step) = await RunAsync(Argument(toolCall, "sql"), context, cancellationToken);
                        steps.Add(step);
                        last = result ?? last;
                        messages.Add(ModelMessage.Tool(toolCall.Id, content));
                        break;

                    case AgentPrompt.DescribeTable:
                        messages.Add(ModelMessage.Tool(toolCall.Id, AgentPrompt.Describe(Argument(toolCall, "table"))));
                        break;

                    case AgentPrompt.Answer:
                        var answer = ParseAnswer(toolCall.Arguments);
                        string? problem = answer is null ? "The answer's arguments weren't valid JSON. Send text and kind."
                            : LooksLikeSql(answer.Text) ? "Don't show SQL, tables or columns to the member. Say it in plain English."
                            : answer.Kind is ChatAnswerKind.Answer or ChatAnswerKind.TooFew && ranQueryNow ? "Wait for the query's results before answering."
                            : answer.Kind is ChatAnswerKind.Answer && last is null ? "Run a query first: answers must come from the results."
                            : null;
                        if (problem is null)
                        {
                            return new AgentResult(Finish(answer!, last, context), steps, call);
                        }

                        messages.Add(ModelMessage.Tool(toolCall.Id, problem));
                        break;

                    default:
                        messages.Add(ModelMessage.Tool(toolCall.Id, $"There's no tool called {toolCall.Name}. Use run_sql, describe_table or answer."));
                        break;
                }
            }
        }

        return new AgentResult(Fallback, steps, ChatOptions.MaxSteps);
    }

    private async Task<(string Content, QueryResult? Result, AgentStep Step)> RunAsync(string? sql, AgentContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var check = SqlGuard.Check(sql);
        if (check.Query is not { } query)
        {
            return ($"Not run: {check.Problem}", null, new AgentStep(sql ?? string.Empty, check.Problem, 0, 0, watch.ElapsedMilliseconds));
        }

        var outcome = await queries.RunAsync(query, context.HistoryFrom, cancellationToken);
        if (outcome.Result is not { } result)
        {
            return ($"The query failed: {outcome.Problem}", null, new AgentStep(sql!, outcome.Problem, 0, 0, watch.ElapsedMilliseconds));
        }

        return (Describe(result), result, new AgentStep(sql!, null, result.Rows.Count, result.HiddenRows, watch.ElapsedMilliseconds));
    }

    /// <summary>The results as the model sees them: hidden figures as "&lt;3", at most <see cref="RowsShownToModel"/> rows.</summary>
    internal static string Describe(QueryResult result)
    {
        var rows = new JsonArray();
        for (var r = 0; r < Math.Min(result.Rows.Count, RowsShownToModel); r++)
        {
            var row = new JsonArray();
            for (var c = 0; c < result.Columns.Count; c++)
            {
                var value = result.Rows[r][c];
                row.Add(result.Hidden[r] && value is null ? JsonValue.Create("<3") : JsonSerializer.SerializeToNode(value, Json));
            }

            rows.Add(row);
        }

        var json = new JsonObject
        {
            ["columns"] = new JsonArray([.. result.Columns.Select(c => (JsonNode?)JsonValue.Create(c))]),
            ["rows"] = rows,
            ["rowCount"] = result.Rows.Count,
            ["hiddenRows"] = result.HiddenRows,
        };
        if (result.Rows.Count > RowsShownToModel)
        {
            json["note"] = $"Only the first {RowsShownToModel} rows are shown; the chart can use all {result.Rows.Count}.";
        }

        if (result.Truncated)
        {
            json["truncated"] = $"Stopped at {ChatOptions.MaxRows} rows. Group more broadly.";
        }

        if (result.Rows.Count > 0 && result.HiddenRows == result.Rows.Count)
        {
            json["allHidden"] = "Every row has fewer than 3 incidents. Answer with kind too_few.";
        }

        return json.ToJsonString(ModelJson);
    }

    /// <summary>The model's answer call, before the card is drawn.</summary>
    internal sealed record ModelAnswer(string Text, ChatAnswerKind Kind, string ChartType, string? Title, string? LabelColumn, string? ValueColumn, bool OpenMap, IReadOnlyList<string> Suggestions);

    internal static ModelAnswer? ParseAnswer(string arguments)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return null;
        }

        var text = StringOf(json?["text"]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var kind = StringOf(json!["kind"])?.ToLowerInvariant() switch
        {
            "too_few" => ChatAnswerKind.TooFew,
            "clarify" => ChatAnswerKind.Clarify,
            "refuse" => ChatAnswerKind.Refused,
            "out_of_scope" => ChatAnswerKind.OutOfScope,
            _ => ChatAnswerKind.Answer,
        };
        var chart = json["chart"];
        var suggestions = json["suggestions"] is JsonArray list
            ? list.Select(StringOf).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Clip(s.Trim(), 80)).Take(3).ToArray()
            : Array.Empty<string>();
        return new ModelAnswer(
            text.Trim(),
            kind,
            StringOf(chart?["type"])?.ToLowerInvariant() ?? "none",
            StringOf(chart?["title"]),
            StringOf(chart?["label_column"]),
            StringOf(chart?["value_column"]),
            json["open_map"] is JsonValue map && map.TryGetValue<bool>(out var open) && open,
            suggestions);
    }

    /// <summary>The answer with its card and map link drawn from the last query's rows.</summary>
    internal static ChatAnswerDto Finish(ModelAnswer answer, QueryResult? last, AgentContext context)
    {
        var card = answer.Kind == ChatAnswerKind.Answer && last is not null ? Cards.Build(answer, last) : null;
        var map = answer.Kind == ChatAnswerKind.Answer && last is not null && (answer.OpenMap || card?.Type == ChatCardType.Places)
            ? Cards.MapLink(last, context)
            : null;
        return new ChatAnswerDto(Clip(answer.Text, 1200), answer.Kind, card, map, answer.Suggestions);
    }

    private static string? Argument(ToolCall call, string name)
    {
        try
        {
            return StringOf(JsonNode.Parse(call.Arguments)?[name]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    private static bool HasDigits(string text) => text.Any(char.IsAsciiDigit);

    /// <summary>True when text shows SQL or the views' names, which members never see.</summary>
    internal static bool LooksLikeSql(string text) => SqlText().IsMatch(text);

    [GeneratedRegex(@"\bSELECT\b[\s\S]*\bFROM\b|\breporting\.|\bGROUP BY\b|\bIsMainType\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlText();
}
