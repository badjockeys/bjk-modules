using System.Net;
using BJK.Modules.Shared.Delivery;

namespace BJK.Modules.SMS.Tests;

/// <summary>Records each request (with its body read up front) and replies from a queue.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHandler Reply(HttpStatusCode status, string? body = null, Action<HttpResponseMessage>? configure = null)
    {
        responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            configure?.Invoke(response);
            return response;
        });
        return this;
    }

    public FakeHandler Throw(Exception exception)
    {
        responses.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType,
            body));
        if (responses.Count == 0)
        {
            throw new InvalidOperationException("No more fake responses queued.");
        }

        return responses.Dequeue()();
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string? Body);

internal static class TestData
{
    public static ProviderRetry NoDelayRetry() => new(maxAttempts: 3, baseDelay: TimeSpan.Zero, TimeProvider.System);
}

internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
