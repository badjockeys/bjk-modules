using System.Text.Json;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Shared.Security;
using Xunit;

namespace BJK.Modules.Chat.Tests;

/// <summary>A model that replies from a script and records what it was sent.</summary>
internal sealed class ScriptedModel : IChatModel
{
    private readonly Queue<ModelMessage> _replies = new();

    public List<IReadOnlyList<ModelMessage>> Calls { get; } = [];

    public ScriptedModel Then(ModelMessage reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    public ScriptedModel ThenTools(params (string Name, object Arguments)[] calls) =>
        Then(new ModelMessage("assistant", null, [.. calls.Select((c, i) => new ToolCall($"call_{_replies.Count}_{i}", c.Name, JsonSerializer.Serialize(c.Arguments)))]));

    public ScriptedModel ThenSql(string sql) => ThenTools((AgentPrompt.RunSql, new { sql }));

    public ScriptedModel ThenAnswer(object answer) => ThenTools((AgentPrompt.Answer, answer));

    public ScriptedModel ThenText(string text) => Then(ModelMessage.Assistant(text));

    public Task<ModelMessage> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        Calls.Add([.. messages]);
        return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : ModelMessage.Assistant(null));
    }

    /// <summary>The last tool message the agent sent back.</summary>
    public string LastToolReply() => Calls[^1].Last(m => m.Role == "tool").Content!;
}

/// <summary>Answers every query with the same columns and rows (after the small-count rule), and records the queries.</summary>
internal sealed class FakeQueries(string[] columns, params object?[][] rows) : IIncidentQueries
{
    public List<(CheckedQuery Query, DateOnly From)> Runs { get; } = [];

    public string? Problem { get; init; }

    public Task<QueryOutcome> RunAsync(CheckedQuery query, DateOnly historyFrom, CancellationToken cancellationToken)
    {
        Runs.Add((query, historyFrom));
        if (Problem is not null)
        {
            return Task.FromResult(new QueryOutcome(null, Problem));
        }

        var copy = rows.Select(r => (object?[])r.Clone()).ToList();
        return Task.FromResult(new QueryOutcome(SmallCounts.Hide(columns, copy, query, false, TimeSpan.FromMilliseconds(3)), null));
    }
}

internal static class Contexts
{
    public static readonly DateOnly Today = new(2026, 10, 23);

    public static AgentContext Premium(NearPlace? near = null) =>
        new(SubscriptionPlan.Premium, Today, Today.AddDays(-365), near);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
