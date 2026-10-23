using BJK.Modules.Shared.Security;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat;

/// <summary>
/// Settings for Ask Bad Jockeys. Section <c>Chat</c>. The bjk_chat login's connection string is separate
/// (<c>ConnectionStrings:BadJockeysChat</c> in bjk-core).
/// </summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>The longest a query may run. Fixed, not a setting.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most rows a query may return. Fixed, not a setting.</summary>
    public const int MaxRows = 1000;

    /// <summary>Grouped counts below this are hidden ("fewer than 3"). Fixed, not a setting.</summary>
    public const int HideCountsUnder = 3;

    /// <summary>The most tool calls the model may make for one question before it has to stop.</summary>
    public const int MaxSteps = 8;

    /// <summary>The longest question accepted, in characters.</summary>
    public const int MaxQuestionLength = 500;

    /// <summary>The model server.</summary>
    public ChatModelOptions Model { get; set; } = new();

    /// <summary>Questions per UK day, by plan. Free members can't ask.</summary>
    public ChatPlanNumbers DailyQuestions { get; set; } = new() { Premium = 20, PremiumPlus = 100 };

    /// <summary>
    /// How far back each plan can look, in days (the same as the heat map: Premium a year, Premium + five years).
    /// Every query is limited to incidents on or after that date.
    /// </summary>
    public ChatPlanNumbers HistoryDays { get; set; } = new() { Premium = 365, PremiumPlus = 1826 };

    /// <summary>True when <c>Chat:Model:Endpoint</c> is set. Without it the chat answers 503 (allowed in Development only).</summary>
    public bool UsesModel => !string.IsNullOrWhiteSpace(Model.Endpoint);
}

/// <summary>
/// Section <c>Chat:Model</c>: an OpenAI-compatible chat completions server. Locally Ollama
/// (<c>http://localhost:11434/v1</c>, model <c>qwen3:8b</c>); live a serverless GPU running Qwen3 32B.
/// </summary>
public sealed class ChatModelOptions
{
    /// <summary>The server's base address, ending <c>/v1</c>; <c>/chat/completions</c> is added to it.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model's name on that server, e.g. <c>qwen3:8b</c> (Ollama) or <c>Qwen/Qwen3-32B</c>.</summary>
    public string Name { get; set; } = "qwen3:8b";

    /// <summary>Secret. Sent as a bearer token when set (Ollama needs none).</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// How long one model call may take, in seconds. Generous because the live GPU switches off when idle and the
    /// first question after a quiet spell waits for it to start.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>A number for each paid plan.</summary>
public sealed class ChatPlanNumbers
{
    public int Premium { get; set; }

    public int PremiumPlus { get; set; }

    /// <summary>The number for a plan, or 0 for Free and logged-out callers.</summary>
    public int For(SubscriptionPlan? plan) => plan switch
    {
        SubscriptionPlan.Premium => Premium,
        SubscriptionPlan.PremiumPlus => PremiumPlus,
        _ => 0,
    };
}

/// <summary>Checks the chat settings at startup.</summary>
internal sealed class ChatOptionsValidator : IValidateOptions<ChatOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatOptions options)
    {
        var failures = new List<string>();
        if (options.UsesModel)
        {
            if (!Uri.TryCreate(options.Model.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add("Chat:Model:Endpoint must be an http or https address, e.g. http://localhost:11434/v1.");
            }

            if (string.IsNullOrWhiteSpace(options.Model.Name))
            {
                failures.Add("Chat:Model:Name is required with Chat:Model:Endpoint, e.g. qwen3:8b.");
            }
        }

        if (options.Model.TimeoutSeconds is < 5 or > 600)
        {
            failures.Add("Chat:Model:TimeoutSeconds must be between 5 and 600.");
        }

        if (options.DailyQuestions.Premium < 1 || options.DailyQuestions.PremiumPlus < options.DailyQuestions.Premium)
        {
            failures.Add("Chat:DailyQuestions must be at least 1 and grow with the plan (Premium ≤ PremiumPlus).");
        }

        if (options.HistoryDays.Premium < 1 || options.HistoryDays.PremiumPlus < options.HistoryDays.Premium)
        {
            failures.Add("Chat:HistoryDays must be at least 1 and grow with the plan (Premium ≤ PremiumPlus).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
