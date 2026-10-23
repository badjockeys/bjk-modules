using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BJK.Modules.Chat.Agent;
using Microsoft.Extensions.Options;
using Xunit;

namespace BJK.Modules.Chat.Tests;

public sealed class ChatModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Reads_tool_calls_with_string_or_object_arguments()
    {
        var reply = OpenAiCompatibleChatModel.Parse(JsonNode.Parse(
            """
            {"choices":[{"message":{"role":"assistant","content":"<think>Use run_sql.</think>","tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"run_sql","arguments":"{\"sql\":\"SELECT 1\"}"}},
              {"type":"function","function":{"name":"describe_table","arguments":{"table":"reporting.Incidents"}}}
            ]}}]}
            """));

        Assert.Null(reply.Content);
        Assert.Equal(2, reply.ToolCalls!.Count);
        Assert.Equal("call_1", reply.ToolCalls[0].Id);
        Assert.Equal("""{"sql":"SELECT 1"}""", reply.ToolCalls[0].Arguments);
        Assert.Equal("describe_table", reply.ToolCalls[1].Name);
        Assert.StartsWith("call_", reply.ToolCalls[1].Id);
        Assert.Contains("reporting.Incidents", reply.ToolCalls[1].Arguments);
    }

    [Theory]
    [InlineData("<think>hmm</think>\n\nSeven reports.", "Seven reports.")]
    [InlineData("<think>still thinking", "")]
    [InlineData("Plain.", "Plain.")]
    public void Strips_thinking(string text, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleChatModel.StripThinking(text));

    [Fact]
    public void A_reply_without_a_message_is_an_outage() =>
        Assert.Throws<ModelUnavailableException>(() => OpenAiCompatibleChatModel.Parse(JsonNode.Parse("""{"error":"loading"}""")));

    [Fact]
    public async Task Posts_the_conversation_and_tools_to_chat_completions()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Hi."}}]}""");
        var options = Options.Create(new ChatOptions { Model = new ChatModelOptions { Endpoint = "https://models.example.test/v1/", Name = "chat-model", ApiKey = "secret" } });
        var model = new OpenAiCompatibleChatModel(new HttpClient(handler), options);

        var reply = await model.CompleteAsync(
            [ModelMessage.System("rules"), ModelMessage.User("q"), new ModelMessage("assistant", null, [new ToolCall("c1", "run_sql", "{}")]), ModelMessage.Tool("c1", "rows")],
            AgentPrompt.Tools,
            Ct);

        Assert.Equal("Hi.", reply.Content);
        Assert.Equal("https://models.example.test/v1/chat/completions", handler.Uri!.ToString());
        Assert.Equal("Bearer secret", handler.Authorization);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("chat-model", body["model"]!.GetValue<string>());
        Assert.Equal(3, body["tools"]!.AsArray().Count);
        Assert.Equal("run_sql", body["messages"]![2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("c1", body["messages"]![3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_errors_and_timeouts_are_outages()
    {
        var options = Options.Create(new ChatOptions { Model = new ChatModelOptions { Endpoint = "https://models.example.test/v1", Name = "chat-model" } });

        var error = new OpenAiCompatibleChatModel(new HttpClient(new RecordingHandler(HttpStatusCode.ServiceUnavailable, "starting")), options);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => error.CompleteAsync([ModelMessage.User("q")], [], Ct));

        var slow = new OpenAiCompatibleChatModel(new HttpClient(new RecordingHandler(new TaskCanceledException("timed out"))), options);
        await Assert.ThrowsAsync<ModelUnavailableException>(() => slow.CompleteAsync([ModelMessage.User("q")], [], Ct));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        private readonly Exception? _throw;

        public RecordingHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        public RecordingHandler(Exception toThrow) => _throw = toThrow;

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throw is not null)
            {
                throw _throw;
            }

            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_body!, Encoding.UTF8, "application/json") };
        }
    }
}
