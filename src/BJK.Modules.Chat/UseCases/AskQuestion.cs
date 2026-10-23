using System.Diagnostics;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Infrastructure;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat.UseCases;

/// <summary>
/// Ask Bad Jockeys a question. Premium and Premium + only, within the day's allowance.
/// </summary>
/// <param name="Question">The question, up to 500 characters.</param>
/// <param name="History">Earlier turns in this chat window, oldest first (the app keeps them; nothing is stored).</param>
/// <param name="Near">Where "near me" means: the member's location or home area, if the app has it.</param>
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
    private readonly ChatAgent _agent;
    private readonly ChatOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<AskQuestionCommandHandler> _logger;

    public AskQuestionCommandHandler(
        ICurrentUser caller,
        IPlanResolver plans,
        IChatUsage usage,
        ChatAgent agent,
        IOptions<ChatOptions> options,
        TimeProvider clock,
        ILogger<AskQuestionCommandHandler> logger)
    {
        _caller = caller;
        _plans = plans;
        _usage = usage;
        _agent = agent;
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

        if (!_options.UsesModel)
        {
            throw new ServiceUnavailableException("Ask Bad Jockeys isn't set up here: Chat:Model:Endpoint is missing.");
        }

        var now = _clock.GetUtcNow();
        var today = UkDay.Today(now);
        var limit = _options.DailyQuestions.For(plan);
        var used = await _usage.TryTakeAsync(userId, today, limit, cancellationToken)
            ?? throw new DailyLimitReachedException(
                plan == SubscriptionPlan.Premium
                    ? $"That's your {limit} questions for today. You can ask again from midnight. Premium + gets {_options.DailyQuestions.PremiumPlus} a day and looks back 5 years."
                    : $"That's your {limit} questions for today. You can ask again from midnight.",
                limit,
                UkDay.NextMidnight(now));

        var context = new AgentContext(plan.Value, today, ChatAllowance.HistoryFrom(_options, plan, today)!.Value, request.Near);
        var watch = Stopwatch.StartNew();
        AgentResult result;
        try
        {
            result = await _agent.AskAsync(request.Question.Trim(), request.History ?? [], context, cancellationToken);
        }
        catch (Exception ex) when (ex is ModelUnavailableException or ServiceUnavailableException)
        {
            // Not the member's fault: the question doesn't count.
            await _usage.GiveBackAsync(userId, today, CancellationToken.None);
            ChatLog.ModelUnavailable(_logger, ex.Message, null);
            throw ex as ServiceUnavailableException
                ?? new ServiceUnavailableException("Ask Bad Jockeys is busy right now. Try again in a minute; that question didn't count.");
        }
        catch (OperationCanceledException)
        {
            await _usage.GiveBackAsync(userId, today, CancellationToken.None);
            throw;
        }

        if (result.Answer.Kind == ChatAnswerKind.Failed)
        {
            await _usage.GiveBackAsync(userId, today, cancellationToken);
            used--;
        }

        ChatLog.Answered(_logger, plan.Value, request.Question, result, watch.Elapsed);

        var usage = ChatAllowance.Describe(_options, plan, used, now);
        return new ChatReplyDto(result.Answer, usage);
    }
}

/// <summary>The caller's allowance for today, and the example questions (for the empty chat and the Free preview).</summary>
public sealed record GetChatUsageQuery : IRequest<ChatUsageDto>;

public sealed class GetChatUsageQueryHandler : IRequestHandler<GetChatUsageQuery, ChatUsageDto>
{
    private readonly ICurrentUser _caller;
    private readonly IPlanResolver _plans;
    private readonly IChatUsage _usage;
    private readonly ChatOptions _options;
    private readonly TimeProvider _clock;

    public GetChatUsageQueryHandler(ICurrentUser caller, IPlanResolver plans, IChatUsage usage, IOptions<ChatOptions> options, TimeProvider clock)
    {
        _caller = caller;
        _plans = plans;
        _usage = usage;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<ChatUsageDto> Handle(GetChatUsageQuery request, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var plan = _plans.CurrentPlan();
        var used = plan is SubscriptionPlan.Premium or SubscriptionPlan.PremiumPlus && _caller.UserId() is { } userId
            ? await _usage.UsedAsync(userId, UkDay.Today(now), cancellationToken)
            : 0;
        return ChatAllowance.Describe(_options, plan, used, now);
    }
}

/// <summary>Works out allowances and date limits from the settings.</summary>
internal static class ChatAllowance
{
    /// <summary>The earliest date a plan can ask about, or null for Free.</summary>
    public static DateOnly? HistoryFrom(ChatOptions options, SubscriptionPlan? plan, DateOnly today) =>
        options.HistoryDays.For(plan) is > 0 and var days ? today.AddDays(-days) : null;

    public static ChatUsageDto Describe(ChatOptions options, SubscriptionPlan? plan, int used, DateTimeOffset now)
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
            ChatExamples.Questions);
    }
}
