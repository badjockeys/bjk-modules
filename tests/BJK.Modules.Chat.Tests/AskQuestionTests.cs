using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Chat.UseCases;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Chat.Tests.Contexts;

namespace BJK.Modules.Chat.Tests;

public sealed class AskQuestionTests
{
    private static readonly Guid UserId = Guid.Parse("dbc96c88-21f4-48f3-86f7-a9f9c1a3ec3b");

    // 23 Oct 2026 23:30 UTC is already 24 Oct in the UK (BST, UTC+1).
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly UkToday = new(2026, 10, 24);

    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly IPlanResolver _plans = Substitute.For<IPlanResolver>();
    private readonly IChatUsage _usage = Substitute.For<IChatUsage>();
    private readonly ChatOptions _options = new() { Model = new ChatModelOptions { Endpoint = "http://localhost:11434/v1" } };

    public AskQuestionTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _plans.CurrentPlan().Returns(SubscriptionPlan.Premium);
        _usage.TryTakeAsync(UserId, UkToday, 20, Arg.Any<CancellationToken>()).Returns(5);
    }

    private AskQuestionCommandHandler Handler(IChatModel model, FakeQueries? queries = null) =>
        new(_caller, _plans, _usage, new ChatAgent(model, queries ?? new FakeQueries(["n"], [10])), Options.Create(_options), new FixedClock(Now), NullLogger<AskQuestionCommandHandler>.Instance);

    private static ScriptedModel Answers() => new ScriptedModel()
        .ThenSql("SELECT COUNT(*) AS n FROM reporting.Incidents WHERE IsMainType = 1")
        .ThenAnswer(new { text = "Ten reports.", kind = "answer", chart = new { type = "number" } });

    [Fact]
    public async Task Answers_and_reports_the_allowance_left()
    {
        var queries = new FakeQueries(["n"], [10]);

        var reply = await Handler(Answers(), queries).Handle(new AskQuestionCommand("How many?"), Ct);

        Assert.Equal("Ten reports.", reply.Answer.Text);
        Assert.Equal(10, reply.Answer.Card!.Points.Single().Value);
        var usage = reply.Usage;
        Assert.Equal(("premium", true, 20, 5, 15), (usage.Plan, usage.CanAsk, usage.Limit, usage.Used, usage.Remaining));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(1)), usage.ResetsAt);
        Assert.Equal(UkToday.AddDays(-365), usage.HistoryFrom);
        Assert.Equal(UkToday.AddDays(-365), queries.Runs.Single().From);
    }

    [Fact]
    public async Task Premium_plus_gets_its_own_limit_and_five_years()
    {
        _plans.CurrentPlan().Returns(SubscriptionPlan.PremiumPlus);
        _usage.TryTakeAsync(UserId, UkToday, 100, Arg.Any<CancellationToken>()).Returns(1);
        var queries = new FakeQueries(["n"], [10]);

        var reply = await Handler(Answers(), queries).Handle(new AskQuestionCommand("How many?"), Ct);

        Assert.Equal(99, reply.Usage.Remaining);
        Assert.Equal(UkToday.AddDays(-1826), queries.Runs.Single().From);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SubscriptionPlan.Free)]
    public async Task Free_members_cant_ask(SubscriptionPlan? plan)
    {
        _plans.CurrentPlan().Returns(plan);

        await Assert.ThrowsAsync<ForbiddenException>(() => Handler(Answers()).Handle(new AskQuestionCommand("q"), Ct));
        await _usage.DidNotReceiveWithAnyArgs().TryTakeAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Without_a_model_the_chat_is_unavailable()
    {
        _options.Model.Endpoint = null;

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => Handler(Answers()).Handle(new AskQuestionCommand("q"), Ct));
    }

    [Fact]
    public async Task The_daily_limit_says_when_it_resets_and_what_premium_plus_gets()
    {
        _usage.TryTakeAsync(UserId, UkToday, 20, Arg.Any<CancellationToken>()).Returns((int?)null);

        var limit = await Assert.ThrowsAsync<DailyLimitReachedException>(() => Handler(Answers()).Handle(new AskQuestionCommand("q"), Ct));

        Assert.Equal(20, limit.Limit);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(1)), limit.ResetsAt);
        Assert.Contains("Premium + gets 100 a day", limit.Message);
    }

    [Fact]
    public async Task A_model_outage_gives_the_question_back()
    {
        var model = Substitute.For<IChatModel>();
        model.CompleteAsync(default!, default!, Ct).ReturnsForAnyArgs<Task<ModelMessage>>(_ => throw new ModelUnavailableException("down"));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Handler(model).Handle(new AskQuestionCommand("q"), Ct));

        Assert.Contains("didn't count", error.Message);
        await _usage.Received(1).GiveBackAsync(UserId, UkToday, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_question_it_couldnt_work_out_doesnt_count()
    {
        var model = new ScriptedModel().ThenText("No idea, maybe 4.").ThenText("Still 4.");

        var reply = await Handler(model).Handle(new AskQuestionCommand("q"), Ct);

        Assert.Equal(ChatAnswerKind.Failed, reply.Answer.Kind);
        Assert.Equal(4, reply.Usage.Used);
        await _usage.Received(1).GiveBackAsync(UserId, UkToday, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Usage_for_free_and_logged_out_callers_is_the_locked_preview()
    {
        _plans.CurrentPlan().Returns((SubscriptionPlan?)null);
        var handler = new GetChatUsageQueryHandler(_caller, _plans, _usage, Options.Create(_options), new FixedClock(Now));

        var usage = await handler.Handle(new GetChatUsageQuery(), Ct);

        Assert.False(usage.CanAsk);
        Assert.Equal(0, usage.Limit);
        Assert.Null(usage.HistoryFrom);
        Assert.NotEmpty(usage.ExampleQuestions);
    }

    [Fact]
    public async Task Usage_for_premium_counts_todays_questions()
    {
        _usage.UsedAsync(UserId, UkToday, Arg.Any<CancellationToken>()).Returns(7);
        var handler = new GetChatUsageQueryHandler(_caller, _plans, _usage, Options.Create(_options), new FixedClock(Now));

        var usage = await handler.Handle(new GetChatUsageQuery(), Ct);

        Assert.True(usage.CanAsk);
        Assert.Equal(13, usage.Remaining);
        Assert.Equal("premium", usage.Plan);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("How many?", true)]
    public void Validates_the_question(string question, bool valid)
    {
        var result = new AskQuestionCommandValidator().Validate(new AskQuestionCommand(question));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Validates_length_history_and_place()
    {
        var validator = new AskQuestionCommandValidator();

        Assert.False(validator.Validate(new AskQuestionCommand(new string('a', ChatOptions.MaxQuestionLength + 1))).IsValid);
        Assert.False(validator.Validate(new AskQuestionCommand("q", [.. Enumerable.Repeat(new ChatTurn("a", "b"), 21)])).IsValid);
        Assert.False(validator.Validate(new AskQuestionCommand("q", null, new NearPlace(91, 0, null))).IsValid);
        Assert.True(validator.Validate(new AskQuestionCommand("q", [new ChatTurn("a", "b")], new NearPlace(53.4, -2.8, "Prescot"))).IsValid);
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
