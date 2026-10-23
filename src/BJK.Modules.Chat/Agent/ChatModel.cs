using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Chat.Agent;

/// <summary>One message in the conversation with the model.</summary>
/// <param name="Role"><c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>.</param>
/// <param name="Content">The text, if any.</param>
/// <param name="ToolCalls">Tools the assistant asked to call.</param>
/// <param name="ToolCallId">For a <c>tool</c> message, the call it answers.</param>
public sealed record ModelMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static ModelMessage System(string content) => new("system", content);

    public static ModelMessage User(string content) => new("user", content);

    public static ModelMessage Assistant(string? content) => new("assistant", content);

    public static ModelMessage Tool(string callId, string content) => new("tool", content, null, callId);
}

/// <summary>A tool call the model made: the tool's name and its arguments as JSON text.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>A tool the model may call, with its JSON Schema parameters.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

/// <summary>The model server is down, starting up for too long, or answered with something unusable.</summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A chat model that can call tools.</summary>
public interface IChatModel
{
    /// <summary>The model's next message: text, tool calls or both.</summary>
    Task<ModelMessage> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken);
}

/// <summary>
/// Calls an OpenAI-compatible <c>/chat/completions</c> endpoint (Ollama locally; vLLM or similar on the serverless
/// GPU live). Qwen3's thinking (<c>&lt;think&gt;...&lt;/think&gt;</c>) is removed from the text.
/// </summary>
internal sealed partial class OpenAiCompatibleChatModel(HttpClient http, IOptions<ChatOptions> options) : IChatModel
{
    /// <summary>Low, for steady SQL; not zero, so a retry after an error can take a different path.</summary>
    internal const double Temperature = 0.2;

    public async Task<ModelMessage> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        var settings = options.Value.Model;
        var body = new JsonObject
        {
            ["model"] = settings.Name,
            ["temperature"] = Temperature,
            ["stream"] = false,
            ["messages"] = new JsonArray([.. messages.Select(ToJson)]),
        };

        // Some servers refuse an empty tools list, so a call without tools (the topic gate) leaves it out.
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. tools.Select(ToJson)]);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Endpoint!.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ModelUnavailableException("The chat model didn't answer in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ModelUnavailableException(
                    $"The chat model answered {(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}");
            }

            JsonNode? json;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new ModelUnavailableException("The chat model sent something that isn't JSON.", ex);
            }

            return Parse(json);
        }
    }

    internal static ModelMessage Parse(JsonNode? json)
    {
        var message = json?["choices"]?[0]?["message"]
            ?? throw new ModelUnavailableException("The chat model's reply had no message.");

        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls)
            {
                var function = call?["function"];
                var name = function?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Arguments are a JSON string in the OpenAI format; some servers send the object itself.
                var arguments = function!["arguments"] switch
                {
                    JsonValue value when value.TryGetValue<string>(out var text) => text,
                    JsonNode node => node.ToJsonString(),
                    null => "{}",
                };
                var id = call!["id"]?.GetValue<string>();
                calls.Add(new ToolCall(string.IsNullOrWhiteSpace(id) ? $"call_{Guid.NewGuid():N}" : id, name, arguments));
            }
        }

        var content = message["content"] is JsonValue c && c.TryGetValue<string>(out var text2) ? StripThinking(text2) : null;
        return new ModelMessage("assistant", string.IsNullOrWhiteSpace(content) ? null : content, calls.Count > 0 ? calls : null);
    }

    /// <summary>Removes Qwen3's <c>&lt;think&gt;</c> blocks (and an unclosed one at the start).</summary>
    internal static string StripThinking(string text) => ThinkBlock().Replace(text, string.Empty).Trim();

    private static JsonObject ToJson(ModelMessage message)
    {
        var json = new JsonObject { ["role"] = message.Role, ["content"] = message.Content ?? string.Empty };
        if (message.ToolCalls is { Count: > 0 } calls)
        {
            json["tool_calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
            })]);
        }

        if (message.ToolCallId is { } id)
        {
            json["tool_call_id"] = id;
        }

        return json;
    }

    private static JsonObject ToJson(ToolDefinition tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = tool.Parameters.DeepClone(),
        },
    };

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}
