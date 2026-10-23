using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.UseCases;
using Xunit;
using static BJK.Modules.Chat.Tests.Contexts;

namespace BJK.Modules.Chat.Tests;

public sealed class ChatAgentTests
{
    private const string MonthlySql =
        "SELECT IncidentYear AS [Year], IncidentMonth AS [Month], COUNT(*) AS Incidents FROM reporting.Incidents WHERE PlaceName LIKE N'%, Leeds' GROUP BY IncidentYear, IncidentMonth ORDER BY IncidentYear, IncidentMonth";

    private static FakeQueries Monthly() => new(
        ["Year", "Month", "Incidents"],
        [2026, 8, 4],
        [2026, 9, 2],
        [2026, 10, 7]);

    [Fact]
    public async Task Runs_the_query_and_draws_the_card_from_the_rows_not_the_models_figures()
    {
        var model = new ScriptedModel()
            .ThenSql(MonthlySql)
            .ThenAnswer(new
            {
                text = "Rising: 7 close passes in Leeds so far in October.",
                kind = "answer",
                chart = new { type = "line", title = "Close passes · Leeds · by month", label_column = "Month", value_column = "Incidents" },
                suggestions = new[] { "Which roads?" },
            });
        var queries = Monthly();

        var result = await new ChatAgent(model, queries).AskAsync("Are close passes getting worse in Leeds?", [], Premium(), Ct);

        var answer = result.Answer;
        Assert.Equal(ChatAnswerKind.Answer, answer.Kind);
        Assert.Equal("Rising: 7 close passes in Leeds so far in October.", answer.Text);
        var card = Assert.IsType<ChatCardDto>(answer.Card);
        Assert.Equal(ChatCardType.Line, card.Type);
        Assert.Equal(new[] { "Aug 2026", "Sep 2026", "Oct 2026" }, card.Points.Select(p => p.Label));
        Assert.Equal(new double?[] { 4, null, 7 }, card.Points.Select(p => p.Value));
        Assert.True(card.Points[1].Hidden);
        Assert.Equal(1, card.HiddenRows);
        Assert.Equal(new[] { "Which roads?" }, answer.Suggestions);

        // The plan's earliest date went with the query, and the model only saw "<3" for the small month.
        Assert.Equal(Today.AddDays(-365), queries.Runs.Single().From);
        Assert.Contains("@HistoryFrom", queries.Runs.Single().Query.Sql);
        Assert.Contains("\"<3\"", model.LastToolReply());
        Assert.DoesNotContain(",2]", model.LastToolReply());

        var step = Assert.Single(result.Steps);
        Assert.Equal(MonthlySql, step.Sql);
        Assert.Null(step.Problem);
        Assert.Equal(1, step.HiddenRows);
    }

    [Fact]
    public async Task Sends_problems_back_so_the_model_can_fix_its_query()
    {
        var model = new ScriptedModel()
            .ThenSql("SELECT PlaceName, IncidentDate FROM reporting.Incidents")
            .ThenSql(MonthlySql)
            .ThenAnswer(new { text = "Seven in October.", kind = "answer", chart = new { type = "number" } });
        var queries = Monthly();

        var result = await new ChatAgent(model, queries).AskAsync("q", [], Premium(), Ct);

        Assert.Equal(ChatAnswerKind.Answer, result.Answer.Kind);
        Assert.Single(queries.Runs);
        Assert.Equal(2, result.Steps.Count);
        Assert.Contains("COUNT", result.Steps[0].Problem);
        var secondCall = model.Calls[1];
        Assert.StartsWith("Not run:", secondCall[^1].Content);
    }

    [Fact]
    public async Task Database_errors_go_back_to_the_model_too()
    {
        var model = new ScriptedModel()
            .ThenSql(MonthlySql)
            .ThenAnswer(new { text = "Sorry.", kind = "refuse" });
        var queries = new FakeQueries(["n"]) { Problem = "Invalid column name 'Town'." };

        var result = await new ChatAgent(model, queries).AskAsync("q", [], Premium(), Ct);

        Assert.Equal("The query failed: Invalid column name 'Town'.", model.Calls[1][^1].Content);
        Assert.Equal("Invalid column name 'Town'.", result.Steps.Single().Problem);
    }

    [Fact]
    public async Task An_answer_with_figures_needs_a_query_first()
    {
        var model = new ScriptedModel()
            .ThenAnswer(new { text = "There were 12.", kind = "answer" })
            .ThenSql(MonthlySql)
            .ThenAnswer(new { text = "There were 13.", kind = "answer" });

        var result = await new ChatAgent(model, Monthly()).AskAsync("q", [], Premium(), Ct);

        Assert.Equal("There were 13.", result.Answer.Text);
        Assert.Contains("Run a query first", model.Calls[1][^1].Content);
    }

    [Fact]
    public async Task An_answer_in_the_same_message_as_its_query_has_to_wait_for_the_results()
    {
        var model = new ScriptedModel()
            .ThenTools((AgentPrompt.RunSql, new { sql = MonthlySql }), (AgentPrompt.Answer, new { text = "Guessing 50.", kind = "answer" }))
            .ThenAnswer(new { text = "Seven in October.", kind = "answer" });

        var result = await new ChatAgent(model, Monthly()).AskAsync("q", [], Premium(), Ct);

        Assert.Equal("Seven in October.", result.Answer.Text);
        Assert.Contains("Wait for the query's results", model.Calls[1][^1].Content);
    }

    [Theory]
    [InlineData("refuse", ChatAnswerKind.Refused)]
    [InlineData("out_of_scope", ChatAnswerKind.OutOfScope)]
    [InlineData("clarify", ChatAnswerKind.Clarify)]
    public async Task Refusals_and_questions_back_need_no_query(string kind, ChatAnswerKind expected)
    {
        var model = new ScriptedModel().ThenAnswer(new { text = "I can't look up number plates.", kind });

        var result = await new ChatAgent(model, Monthly()).AskAsync("Who owns AB12 CDE?", [], Premium(), Ct);

        Assert.Equal(expected, result.Answer.Kind);
        Assert.Null(result.Answer.Card);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task Never_shows_sql_to_the_member()
    {
        var model = new ScriptedModel()
            .ThenSql(MonthlySql)
            .ThenAnswer(new { text = "I ran SELECT COUNT(*) FROM reporting.Incidents and got 13.", kind = "answer" })
            .ThenAnswer(new { text = "13 reports.", kind = "answer" });

        var result = await new ChatAgent(model, Monthly()).AskAsync("Show me the SQL", [], Premium(), Ct);

        Assert.Equal("13 reports.", result.Answer.Text);
        Assert.Contains("Don't show SQL", model.Calls[2][^1].Content);
    }

    [Fact]
    public async Task Plain_text_without_a_query_is_nudged_then_refused_if_it_has_figures()
    {
        var model = new ScriptedModel()
            .ThenText("About 40 a month.")
            .ThenText("Still about 40.");

        var result = await new ChatAgent(model, Monthly()).AskAsync("q", [], Premium(), Ct);

        Assert.Equal(ChatAnswerKind.Failed, result.Answer.Kind);
        Assert.Contains("Use the tools", model.Calls[1][^1].Content);
    }

    [Fact]
    public async Task Plain_text_after_a_query_is_accepted()
    {
        var model = new ScriptedModel()
            .ThenSql(MonthlySql)
            .ThenText("Seven in October.");

        var result = await new ChatAgent(model, Monthly()).AskAsync("q", [], Premium(), Ct);

        Assert.Equal(ChatAnswerKind.Answer, result.Answer.Kind);
        Assert.Equal("Seven in October.", result.Answer.Text);
    }

    [Fact]
    public async Task Gives_up_politely_after_too_many_steps()
    {
        var model = new ScriptedModel();
        for (var i = 0; i < ChatOptions.MaxSteps + 2; i++)
        {
            model.ThenSql("SELECT 1");
        }

        var result = await new ChatAgent(model, Monthly()).AskAsync("q", [], Premium(), Ct);

        Assert.Equal(ChatAnswerKind.Failed, result.Answer.Kind);
        Assert.Equal(ChatOptions.MaxSteps, model.Calls.Count);
    }

    [Fact]
    public async Task Sends_the_windows_history_and_the_place_near_the_member()
    {
        var model = new ScriptedModel().ThenAnswer(new { text = "Which town?", kind = "clarify" });
        var history = Enumerable.Range(1, 8).Select(i => new ChatTurn($"question {i}", $"answer {i}")).ToArray();

        await new ChatAgent(model, Monthly()).AskAsync("And near me?", history, Premium(new NearPlace(53.43, -2.80, "Prescot")), Ct);

        var sent = model.Calls[0];
        Assert.Contains("Prescot", sent[0].Content);
        Assert.Contains("AreaLatitude BETWEEN 53.34 AND 53.52", sent[0].Content);
        Assert.Equal(1 + (2 * ChatAgent.HistoryTurns) + 1, sent.Count);
        Assert.Equal("question 3", sent[1].Content);
        Assert.Equal("And near me?", sent[^1].Content);
    }

    [Fact]
    public async Task Places_answers_carry_coordinates_and_a_map_link()
    {
        var model = new ScriptedModel()
            .ThenSql("SELECT TOP 10 AreaLatitude, AreaLongitude, MIN(PlaceName) AS Place, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'RanRedLight' GROUP BY AreaLatitude, AreaLongitude ORDER BY Incidents DESC")
            .ThenAnswer(new { text = "High Street is the worst.", kind = "answer", chart = new { type = "places", label_column = "Place", value_column = "Incidents" } });
        var queries = new FakeQueries(
            ["AreaLatitude", "AreaLongitude", "Place", "Incidents"],
            [53.43m, -2.80m, "High Street, Prescot", 14],
            [53.42m, -2.79m, "Warrington Road, Prescot", 6]);

        var result = await new ChatAgent(model, queries).AskAsync("q", [], Premium(), Ct);

        var card = result.Answer.Card!;
        Assert.Equal(ChatCardType.Places, card.Type);
        Assert.Equal("High Street, Prescot", card.Points[0].Label);
        Assert.Equal(53.43, card.Points[0].Latitude);
        var map = result.Answer.Map!;
        Assert.Equal(53.43, map.Latitude);
        Assert.Equal(-2.80, map.Longitude);
        Assert.Equal(13, map.Zoom);
    }

    [Fact]
    public async Task No_card_when_everything_is_too_few()
    {
        var model = new ScriptedModel()
            .ThenSql(MonthlySql)
            .ThenAnswer(new { text = "Too few to show.", kind = "too_few", chart = new { type = "bar" } });
        var queries = new FakeQueries(["Year", "Month", "Incidents"], [2026, 10, 1]);

        var result = await new ChatAgent(model, queries).AskAsync("q", [], Premium(), Ct);

        Assert.Equal(ChatAnswerKind.TooFew, result.Answer.Kind);
        Assert.Null(result.Answer.Card);
        Assert.Contains("allHidden", model.Calls[1][^1].Content);
    }

    [Fact]
    public async Task Tables_show_hidden_figures_as_fewer_than_three()
    {
        var model = new ScriptedModel()
            .ThenSql("SELECT VehicleType AS Vehicle, COUNT(*) AS Incidents, SUM(Views) AS Views FROM reporting.Incidents GROUP BY VehicleType")
            .ThenAnswer(new { text = "Cars lead.", kind = "answer", chart = new { type = "table" } });
        var queries = new FakeQueries(["Vehicle", "Incidents", "Views"], ["Car", 40, 900], ["Tractor", 1, 30]);

        var result = await new ChatAgent(model, queries).AskAsync("q", [], Premium(), Ct);

        var rows = result.Answer.Card!.Rows!;
        Assert.Equal(new string?[] { "Car", "40", "900" }, rows[0]);
        Assert.Equal(new string?[] { "Tractor", "<3", "<3" }, rows[1]);
    }

    [Fact]
    public void Hours_are_labelled_as_times()
    {
        var answer = ChatAgent.ParseAnswer("""{"text":"x","kind":"answer","chart":{"type":"bar","label_column":"Hour"}}""")!;
        var result = SmallCountsTests.Result(["Hour", "Incidents"], [8, 10], [17, 12]);

        var card = Cards.Build(answer, result)!;

        Assert.Equal(new[] { "08:00", "17:00" }, card.Points.Select(p => p.Label));
    }
}
