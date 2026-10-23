using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Sql;
using BJK.Modules.Chat.UseCases;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace BJK.Modules.Chat.Tests.Evaluation;

/// <summary>
/// The question set (<c>questions.json</c>) and the evaluation run. The run is skipped unless
/// <c>BJK_CHAT_EVAL_ENDPOINT</c> and <c>BJK_CHAT_EVAL_DATABASE</c> are set; see Evaluation/README.md.
/// </summary>
public sealed class EvaluationTests
{
    /// <summary>The member's place for questions marked "near": Prescot.</summary>
    private static readonly NearPlace Prescot = new(53.43, -2.80, "Prescot");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal sealed record EvalQuestion(int Id, string Question, string Expect, string Topic, bool Near);

    private sealed record EvalFile(IReadOnlyList<EvalQuestion> Questions);

    internal static IReadOnlyList<EvalQuestion> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Evaluation", "questions.json");
        return JsonSerializer.Deserialize<EvalFile>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Questions;
    }

    [Fact]
    public void The_question_set_is_well_formed()
    {
        var questions = Load();

        Assert.True(questions.Count >= 200, $"Only {questions.Count} questions.");
        Assert.Equal(questions.Count, questions.Select(q => q.Id).Distinct().Count());
        Assert.All(questions, q =>
        {
            Assert.Contains(q.Expect, new[] { "answer", "refuse", "out_of_scope", "clarify", "too_few" });
            Assert.InRange(q.Question.Length, 3, ChatOptions.MaxQuestionLength);
        });
    }

    /// <summary>Whether a reply is the kind the question expects.</summary>
    internal static bool Passes(EvalQuestion question, AgentResult result) => question.Expect switch
    {
        "answer" => result.Answer.Kind is ChatAnswerKind.Answer or ChatAnswerKind.TooFew && result.Steps.Any(s => s.Problem is null),
        "refuse" or "out_of_scope" => result.Answer.Kind is ChatAnswerKind.Refused or ChatAnswerKind.OutOfScope,
        "clarify" => result.Answer.Kind == ChatAnswerKind.Clarify,
        "too_few" => result.Answer.Kind is ChatAnswerKind.TooFew or ChatAnswerKind.Answer,
        _ => false,
    };

    [Fact]
    public async Task Run_the_question_set_against_a_real_model()
    {
        var endpoint = Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_ENDPOINT");
        var database = Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_DATABASE");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(database))
        {
            Assert.Skip("Set BJK_CHAT_EVAL_ENDPOINT and BJK_CHAT_EVAL_DATABASE to run the evaluation.");
        }

        var modelName = Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_MODEL") ?? "qwen3:8b";
        var options = Options.Create(new ChatOptions
        {
            Model = new ChatModelOptions
            {
                Endpoint = endpoint,
                Name = modelName,
                ApiKey = Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_API_KEY"),
                // TMP: CPU calls can take minutes; measure them rather than time out.
                TimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_TIMEOUT"), out var t) ? t : 120,
            },
        });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.Value.Model.TimeoutSeconds) };
        var agent = new ChatAgent(new OpenAiCompatibleChatModel(http, options), new SqlIncidentQueries(new ChatDatabase(database)));

        var questions = Load();
        if (int.TryParse(Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_LIMIT"), out var limit) && limit > 0)
        {
            questions = [.. questions.Take(limit)];
        }

        // TMP: every Nth question, for a sample across all topics.
        if (int.TryParse(Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_EVERY"), out var every) && every > 1)
        {
            questions = [.. questions.Where((_, i) => i % every == 0)];
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var path = Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_REPORT")
            ?? Path.Combine(Path.GetTempPath(), $"ask-bad-jockeys-eval-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md");
        if (Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_PROMPT") is { Length: > 0 } promptFile)
        {
            await File.WriteAllTextAsync(promptFile, AgentPrompt.System(new AgentContext(SubscriptionPlan.PremiumPlus, today, today.AddDays(-1826), null)), Ct);
        }

        var rows = new List<(EvalQuestion Question, AgentResult? Result, string? Error, TimeSpan Took, bool Passed)>();
        foreach (var question in questions)
        {
            var context = new AgentContext(SubscriptionPlan.PremiumPlus, today, today.AddDays(-1826), question.Near ? Prescot : null);
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await agent.AskAsync(question.Question, [], context, Ct);
                rows.Add((question, result, null, watch.Elapsed, Passes(question, result)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rows.Add((question, null, ex.Message, watch.Elapsed, false));
            }

            // TMP: the report after every question, so a run cut short still has one.
            await File.WriteAllTextAsync(path, Report(modelName, rows), Ct);
        }

        await File.WriteAllTextAsync(path, Report(modelName, rows), Ct);
        TestContext.Current.SendDiagnosticMessage($"Evaluation report: {path}");

        if (double.TryParse(Environment.GetEnvironmentVariable("BJK_CHAT_EVAL_MIN_PASS"), CultureInfo.InvariantCulture, out var minimum))
        {
            var passed = rows.Count(r => r.Passed) / (double)rows.Count;
            Assert.True(passed >= minimum, $"Passed {passed:P0}, below {minimum:P0}. See {path}.");
        }
    }

    private static string Report(string model, List<(EvalQuestion Question, AgentResult? Result, string? Error, TimeSpan Took, bool Passed)> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Ask Bad Jockeys evaluation: {model}, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Passed {rows.Count(r => r.Passed)} of {rows.Count}. Median time {Median(rows.Select(r => r.Took.TotalSeconds)):0.0} s.");
        text.AppendLine();
        text.AppendLine("| Topic | Passed | Of | Avg queries | Rejected or failed queries |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var topic in rows.GroupBy(r => r.Question.Topic))
        {
            var steps = topic.SelectMany(r => r.Result?.Steps ?? []).ToArray();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {topic.Key} | {topic.Count(r => r.Passed)} | {topic.Count()} | {topic.Average(r => r.Result?.Steps.Count ?? 0):0.0} | {steps.Count(s => s.Problem is not null)} |");
        }

        text.AppendLine();
        text.AppendLine("## Each question");
        foreach (var (question, result, error, took, passed) in rows)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"### {(passed ? "✅" : "❌")} {question.Id}. {question.Question} (expected {question.Expect}, {took.TotalSeconds:0.0} s)");
            if (error is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Error: {error}");
                continue;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"{result!.Answer.Kind}: {result.Answer.Text}");
            if (result.Answer.Card is { } card)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"Card: {card.Type} \"{card.Title}\" — {string.Join(", ", card.Points.Take(6).Select(p => $"{p.Label}: {(p.Hidden ? "<3" : p.Value?.ToString(CultureInfo.InvariantCulture))}"))}");
            }

            foreach (var step in result.Steps)
            {
                text.AppendLine();
                text.AppendLine("```sql");
                text.AppendLine(step.Sql);
                text.AppendLine("```");
                text.AppendLine(step.Problem is null
                    ? string.Create(CultureInfo.InvariantCulture, $"{step.Rows} rows ({step.HiddenRows} hidden), {step.Milliseconds} ms")
                    : $"Problem: {step.Problem}");
            }
        }

        return text.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}
