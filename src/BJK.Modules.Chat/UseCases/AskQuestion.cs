using System.Diagnostics;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Guard;
using BJK.Modules.Chat.Infrastructure;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat.UseCases;

/// <summary>
/// Ask Bad Jockeys a question. Premium and Premium + only, within the day's allowance, and through the guard rails
/// (<see cref="ChatGuard"/>): blocked questions don't use the allowance but count towards <c>Chat:MisuseLimit</c>.
/// </summary>
/// <param name="Question">The question, up to 500 characters.</param>
/// <param name="History">Earlier turns in this chat window, oldest first (the app keeps them; nothing is stored).</param>
/// <param name="Near">
/// Where "near me" means: the member's location, if the app has it. Without it, their home area (if they've chosen one).
/// </param>
public sealed record AskQuestionCommand(string Question, IReadOnlyList<ChatTurn>? History = null, NearPlace? Near = null) : IRequest<ChatReplyDto>;

public sealed class AskQuestionCommandValidator : AbstractValidator<AskQuestionCommand>
{
    public AskQuestionCommandValidator()
    {
        RuleFor(c => c.Question).NotEmpty().MaximumLength(ChatOptions.MaxQuestionLength);
        RuleFor(c => c.History).Must(h => h is null || h.Count <= 20).WithMessage("Send at most 20 earlier turns.");
        RuleForEach(c => c.History).ChildRules(turn =>
        {
            turn.RuleFor(t => t.Question).NotNull().MaximumLength(ChatOptions.MaxQuestionLength);
            turn.RuleFor(t => t.Answer).NotNull().MaximumLength(2000);
            turn.RuleFor(t => t.Token).MaximumLength(100);
        });
        When(c => c.Near is not null, () =>
        {
            RuleFor(c => c.Near!.Latitude).InclusiveBetween(-90, 90);
            RuleFor(c => c.Near!.Longitude).InclusiveBetween(-180, 180);
            RuleFor(c => c.Near!.Name).MaximumLength(100);
        });
    }
}

public sealed class AskQuestionCommandHandler : IRequestHandler<AskQuestionCommand, ChatReplyDto>
{
    private readonly ICurrentUser _caller;
    private readonly IPlanResolver _plans;
    private readonly IChatUsage _usage;
    private readonly ChatGuard _guard;
    private readonly TurnTokens _tokens;
    private readonly ChatAgent _agent;
    private readonly IHomeAreas _homeAreas;
    private readonly ChatOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AskQuestionCommandHandler> _logger;

    public AskQuestionCommandHandler(
        ICurrentUser caller,
        IPlanResolver plans,
        IChatUsage usage,
        ChatGuard guard,
        TurnTokens tokens,
        ChatAgent agent,
        IHomeAreas homeAreas,
        IOptions<ChatOptions> options,
        TimeProvider clock,
        ILogger<AskQuestionCommandHandler> logger)
    {
        _caller = caller;
        _plans = plans;
        _usage = usage;
        _guard = guard;
        _tokens = tokens;
        _agent = agent;
        _homeAreas = homeAreas;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ChatReplyDto> Handle(AskQuestionCommand request, CancellationToken cancellationToken)
    {
        var userId = _caller.RequireUserId();
        var plan = _plans.CurrentPlan();
        if (plan is not (SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus))
        {
            throw new ForbiddenException("Ask Bad Jockeys is part of Premium.");
        }

        // The placeholder while no model is chosen: log it (no member id, no question text) and don't count it.
        if (!_options.UsesModel)
        {
            ChatLog.NotSwitchedOn(_logger, plan);
            throw new ServiceUnavailableException("Ask Bad Jockeys isn't switched on yet. That question didn't count.");
        }

        var now = _clock.GetUtcNow();
        var today = UkDay.Today(now);
        var limit = _options.DailyQuestions.For(plan);
        var counts = await _usage.GetAsync(userId, today, cancellationToken);
        if (counts.Strikes >= _options.MisuseLimit)
        {
            throw Paused();
        }

        if (counts.Questions >= limit)
        {
            throw LimitReached(plan.Value, limit, now);
        }

        // The home area tailors the suggestions and, when the app sends no place, says where "near me" is.
        var home = await _homeAreas.GetAsync(userId, cancellationToken);
        var examples = ChatExamples.For(home);

        // History is only what we signed for this member; anything else is dropped before the guard or model sees it.
        var question = request.Question.Trim();
        var history = _tokens.Verified(userId, request.History);

        // The guard rails: fixed checks, the content screener and the topic gate. Blocked questions don't use the
        // allowance, but misuse counts as a strike.
        GuardVerdict verdict;
        try
        {
            verdict = await _guard.CheckQuestionAsync(question, history.Count > 0 ? history[^1].Question : null, examples, cancellationToken);
        }
        catch (ModelUnavailableException ex)
        {
            ChatLog.ModelUnavailable(_logger, ex.Message, null);
            throw Busy();
        }

        if (verdict.Reply is { } stopped)
        {
            var strikes = counts.Strikes;
            if (verdict.IsStrike)
            {
                strikes = await _usage.AddStrikeAsync(userId, today, cancellationToken);
                ChatLog.Blocked(_logger, plan.Value, verdict.Reason.ToString(), verdict.Reason == GuardReason.PersonalDetails ? "(withheld)" : question);
            }

            return new ChatReplyDto(stopped, ChatAllowance.Describe(_options, plan, counts.Questions, now, examples, strikes));
        }

        var used = await _usage.TryTakeAsync(userId, today, limit, cancellationToken) ?? throw LimitReached(plan.Value, limit, now);
        var near = request.Near ?? ChatExamples.NearOf(home);
        var context = new AgentContext(plan.Value, today, ChatAllowance.HistoryFrom(_options, plan, today)!.Value, near);
        var watch = Stopwatch.StartNew();
        AgentResult result;
        try
        {
            result = await _agent.AskAsync(question, history, context, cancellationToken);
        }
        catch (Exception ex) when (ex is ModelUnavailableException or ServiceUnavailableException)
        {
            // Not the member's fault: the question doesn't count.
            await _usage.GiveBackAsync(userId, today, CancellationToken.None);
            ChatLog.ModelUnavailable(_logger, ex.Message, null);
            throw ex as ServiceUnavailableException ?? Busy();
        }
        catch (OperationCanceledException)
        {
            await _usage.GiveBackAsync(userId, today, CancellationToken.None);
            throw;
        }

        // The reply is checked too: no personal details, links, SQL or abuse ever reach the member.
        if (result.Answer.Kind is ChatAnswerKind.Answer or ChatAnswerKind.TooFew or ChatAnswerKind.Clarify
            && !await _guard.CheckAnswerAsync(result.Answer, cancellationToken))
        {
            result = result with { Answer = ChatAgent.Fallback };
        }

        var strikesNow = counts.Strikes;
        if (result.Answer.Kind is ChatAnswerKind.Refused or ChatAnswerKind.OutOfScope)
        {
            // The agent itself declined: treated like a blocked question.
            await _usage.GiveBackAsync(userId, today, cancellationToken);
            used--;
            strikesNow = await _usage.AddStrikeAsync(userId, today, cancellationToken);
            ChatLog.Blocked(_logger, plan.Value, $"Agent{result.Answer.Kind}", question);
        }
        else if (result.Answer.Kind == ChatAnswerKind.Failed)
        {
            await _usage.GiveBackAsync(userId, today, cancellationToken);
            used--;
        }

        ChatLog.Answered(_logger, plan.Value, question, result, watch.Elapsed);

        var token = result.Answer.Kind is ChatAnswerKind.Answer or ChatAnswerKind.TooFew or ChatAnswerKind.Clarify
            ? _tokens.Sign(userId, question, result.Answer.Text)
            : null;
        return new ChatReplyDto(result.Answer, ChatAllowance.Describe(_options, plan, used, now, examples, strikesNow), token);
    }

    private DailyLimitReachedException LimitReached(SubscriptionPlan plan, int limit, DateTimeOffset now) => new(
        plan == SubscriptionPlan.Premium
            ? $"That's your {limit} questions for today. You can ask again from midnight. Premium + gets {_options.DailyQuestions.PremiumPlus} a day and looks back 5 years."
            : $"That's your {limit} questions for today. You can ask again from midnight.",
        limit,
        UkDay.NextMidnight(now));

    private static TooManyRequestsException Paused() => new(
        "Ask Bad Jockeys is paused until midnight: it's had too many questions today that it can't help with. It only answers questions about bad driving and parking reported on Bad Jockeys.",
        ChatAllowance.PausedCode);

    private static ServiceUnavailableException Busy() =>
        new("Ask Bad Jockeys is busy right now. Try again in a minute; that question didn't count.");
}

/// <summary>
/// The caller's allowance for today, and the example questions (for the empty chat and the Free preview), tailored to
/// their home area when they've chosen one.
/// </summary>
public sealed record GetChatUsageQuery : IRequest<ChatUsageDto>;

public sealed class GetChatUsageQueryHandler : IRequestHandler<GetChatUsageQuery, ChatUsageDto>
{
    private readonly ICurrentUser _caller;
    private readonly IPlanResolver _plans;
    private readonly IChatUsage _usage;
    private readonly IHomeAreas _homeAreas;
    private readonly ChatOptions _options;
    private readonly TimeProvider _clock;

    public GetChatUsageQueryHandler(
        ICurrentUser caller,
        IPlanResolver plans,
        IChatUsage usage,
        IHomeAreas homeAreas,
        IOptions<ChatOptions> options,
        TimeProvider clock)
    {
        _caller = caller;
        _plans = plans;
        _usage = usage;
        _homeAreas = homeAreas;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<ChatUsageDto> Handle(GetChatUsageQuery request, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var plan = _plans.CurrentPlan();
        var userId = _caller.UserId();
        var counts = plan is SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus && userId is { } member
            ? await _usage.GetAsync(member, UkDay.Today(now), cancellationToken)
            : DailyCounts.None;

        // Free members get tailored examples too: the locked preview shows what they could ask about their own area.
        var home = userId is { } id ? await _homeAreas.GetAsync(id, cancellationToken) : null;
        return ChatAllowance.Describe(_options, plan, counts.Questions, now, ChatExamples.For(home), counts.Strikes);
    }
}

/// <summary>Works out allowances and date limits from the settings.</summary>
internal static class ChatAllowance
{
    /// <summary>The 429 code when the misuse limit is reached.</summary>
    public const string PausedCode = "chat_paused";

    /// <summary>The earliest date a plan can ask about, or null for Free.</summary>
    public static DateOnly? HistoryFrom(ChatOptions options, SubscriptionPlan? plan, DateOnly today) =>
        options.HistoryDays.For(plan) is > 0 and var days ? today.AddDays(-days) : null;

    public static ChatUsageDto Describe(
        ChatOptions options,
        SubscriptionPlan? plan,
        int used,
        DateTimeOffset now,
        IReadOnlyList<string> examples,
        int strikes = 0)
    {
        var limit = options.DailyQuestions.For(plan);
        var canAsk = limit > 0;
        return new ChatUsageDto(
            plan is { } p ? SubscriptionPlanClaims.ToClaimValue(p) : null,
            canAsk,
            limit,
            used,
            Math.Max(0, limit - used),
            UkDay.NextMidnight(now),
            HistoryFrom(options, plan, UkDay.Today(now)),
            examples,
            canAsk && strikes >= options.MisuseLimit);
    }
}
