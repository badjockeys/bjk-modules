using System.Net;

namespace BJK.Modules.Shared.Tests;

/// <summary>Records each request (with its body read up front) and replies from a queue.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHandler Reply(HttpStatusCode status, string? body = null)
    {
        responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, System.Text.Encoding.UTF8, "application/json"),
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.RequestUri!, body));
        if (responses.Count == 0)
        {
            throw new InvalidOperationException("No more fake responses queued.");
        }

        return responses.Dequeue()();
    }
}

internal sealed record RecordedRequest(Uri Uri, string? Body);
