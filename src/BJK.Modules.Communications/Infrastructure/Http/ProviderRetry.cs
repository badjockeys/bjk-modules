using System.Net;
using BJK.Modules.Communications.Contracts;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Communications.Infrastructure.Http;

/// <summary>
/// Retries a provider call through brief outages: timeouts, connection failures, 429 and 5xx. Anything else
/// (400, 401, 403…) is returned straight away so the sender can report why it was rejected.
/// </summary>
internal sealed class ProviderRetry(int maxAttempts, TimeSpan baseDelay, TimeProvider time)
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    public ProviderRetry(TimeProvider time)
        : this(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(500), time)
    {
    }

    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>
    /// Sends the request built by <paramref name="createRequest"/> (a fresh one per attempt) and returns the first
    /// response that isn't worth retrying. The caller owns and disposes the response.
    /// </summary>
    /// <exception cref="MessageDeliveryException">Still failing after the last attempt (transient).</exception>
    public async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        string provider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string failure;
            Exception? error = null;
            try
            {
                using var request = createRequest();
                var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!IsTransient(response.StatusCode))
                {
                    return response;
                }

                failure = $"HTTP {(int)response.StatusCode}";
                retryAfter = RetryAfter(response);
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                failure = "connection failed";
                error = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout, not the caller giving up.
                failure = "timed out";
                error = ex;
            }

            if (attempt >= MaxAttempts)
            {
                throw new MessageDeliveryException(
                    $"{provider} was unavailable ({failure}) after {MaxAttempts} attempts.",
                    isTransient: true,
                    error);
            }

            var delay = retryAfter ?? Backoff(attempt);
            logger.LogWarning(
                error,
                "{Provider} request failed ({Failure}); attempt {Attempt} of {MaxAttempts}, retrying in {Delay}.",
                provider,
                failure,
                attempt,
                MaxAttempts,
                delay);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private TimeSpan Backoff(int attempt) =>
        baseDelay * Math.Pow(2, attempt - 1) * (0.8 + (Random.Shared.NextDouble() * 0.4));

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta
            ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        if (wait is null || wait < TimeSpan.Zero)
        {
            return null;
        }

        return wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }
}
