using BJK.Modules.Chat.Agent;
using BJK.Modules.Shared.Security;
using Xunit;

namespace BJK.Modules.Chat.Tests;

public sealed class ChatSettingsAndPromptTests
{
    [Fact]
    public void Defaults_are_valid_and_match_the_plan()
    {
        var options = new ChatOptions();

        Assert.True(new ChatOptionsValidator().Validate(null, options).Succeeded);
        Assert.False(options.UsesModel);
        Assert.Equal((20, 100), (options.DailyQuestions.Premium, options.DailyQuestions.PremiumPlus));
        Assert.Equal((365, 1826), (options.HistoryDays.Premium, options.HistoryDays.PremiumPlus));
        Assert.Equal(0, options.DailyQuestions.For(SubscriptionPlan.Free));
        Assert.Equal(0, options.DailyQuestions.For(null));
    }

    [Theory]
    [InlineData("localhost:11434", "qwen3:8b", 120, 20, 100, "Endpoint")]
    [InlineData("http://localhost:11434/v1", "", 120, 20, 100, "Name")]
    [InlineData("http://localhost:11434/v1", "qwen3:8b", 1, 20, 100, "TimeoutSeconds")]
    [InlineData("http://localhost:11434/v1", "qwen3:8b", 120, 0, 100, "DailyQuestions")]
    [InlineData("http://localhost:11434/v1", "qwen3:8b", 120, 50, 20, "DailyQuestions")]
    public void Refuses_bad_settings(string endpoint, string name, int timeout, int premium, int premiumPlus, string mentions)
    {
        var options = new ChatOptions
        {
            Model = new ChatModelOptions { Endpoint = endpoint, Name = name, TimeoutSeconds = timeout },
            DailyQuestions = new ChatPlanNumbers { Premium = premium, PremiumPlus = premiumPlus },
        };

        var result = new ChatOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(mentions, result.FailureMessage);
    }

    [Theory]
    [InlineData(0, null, "MisuseLimit")]
    [InlineData(51, null, "MisuseLimit")]
    [InlineData(5, "too-short", "HistoryKey")]
    public void Refuses_bad_guard_settings(int misuseLimit, string? historyKey, string mentions)
    {
        var result = new ChatOptionsValidator().Validate(null, new ChatOptions { MisuseLimit = misuseLimit, HistoryKey = historyKey });

        Assert.True(result.Failed);
        Assert.Contains(mentions, result.FailureMessage);
    }

    [Fact]
    public void The_prompt_has_the_date_plan_and_every_subcategory()
    {
        var context = new AgentContext(SubscriptionPlan.PremiumPlus, new DateOnly(2026, 10, 23), new DateOnly(2021, 10, 22), null);

        var prompt = AgentPrompt.System(context);

        Assert.Contains("Today is Friday 2026-10-23 (UK)", prompt);
        Assert.Contains("This month started 2026-10-01", prompt);
        Assert.Contains("The last 12 months start 2025-11-01", prompt);
        Assert.Contains("Premium +, which looks back to 2021-10-22", prompt);
        Assert.Contains("answer with kind clarify", prompt);
        Assert.Equal(74, AgentPrompt.SubcategoryList.Split(';', '\n').Count(part => part.Contains(" = ", StringComparison.Ordinal)));
        Assert.Contains("ClosePassCyclist = Close pass of a cyclist", prompt);
        Assert.Contains("PlaceName LIKE N'%, Prescot'", prompt);
        Assert.DoesNotContain("{NEAR}", prompt);
        Assert.DoesNotContain("{TODAY}", prompt);
    }

    [Fact]
    public void Near_me_becomes_a_10_km_box()
    {
        var context = new AgentContext(SubscriptionPlan.Premium, new DateOnly(2026, 10, 23), new DateOnly(2025, 10, 23), new NearPlace(53.4311, -2.8023, "Prescot"));

        var prompt = AgentPrompt.System(context);

        Assert.Contains("near Prescot (53.431, -2.802)", prompt);
        Assert.Contains("AreaLatitude BETWEEN 53.34 AND 53.52 AND AreaLongitude BETWEEN -2.95 AND -2.65", prompt);
    }

    [Theory]
    [InlineData("reporting.Incidents", "IsMainType bit")]
    [InlineData("reporting.Subcategories", "74 rows in 13 groups")]
    [InlineData("posts.Posts", "Only reporting.Incidents and reporting.Subcategories exist")]
    public void Describes_the_views(string table, string mentions) =>
        Assert.Contains(mentions, AgentPrompt.Describe(table));

    [Fact]
    public void Every_tool_has_a_schema() =>
        Assert.All(AgentPrompt.Tools, tool => Assert.Equal("object", tool.Parameters["type"]!.GetValue<string>()));
}
