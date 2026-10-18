using System.Text.Json;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BJK.Modules.Media.Infrastructure.Processing;

/// <summary>
/// Reads processing jobs from the <c>media.process</c> queue and runs each through <see cref="MediaProcessor"/>.
/// Several workers can run side by side; RabbitMQ gives each job to one of them. A job that keeps failing is tried
/// <see cref="MediaQueueTopology.MaxDeliveries"/> times; the last attempt marks the media Failed, so nothing is left
/// stuck at Processing.
/// </summary>
internal sealed class MediaProcessingWorker(
    IRabbitMqConnection rabbit,
    IServiceScopeFactory scopes,
    IOptions<MediaQueueOptions> queueOptions,
    IOptions<MediaProcessingOptions> processingOptions,
    ILogger<MediaProcessingWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = queueOptions.Value.QueueName;
        var concurrency = (ushort)processingOptions.Value.MaxConcurrentJobs;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbit.GetAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: false,
                        publisherConfirmationTrackingEnabled: false,
                        consumerDispatchConcurrency: concurrency),
                    stoppingToken);
                await MediaQueueTopology.DeclareAsync(channel, queue, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: concurrency, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
                logger.LogInformation("Processing media jobs from '{Queue}', {Count} at a time.", queue, concurrency);

                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The media worker lost its queue connection; reconnecting in {Delay}.", ReconnectDelay);
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        MediaProcessingJob? job;
        try
        {
            job = JsonSerializer.Deserialize<MediaProcessingJob>(delivery.Body.Span, RabbitMqMediaJobQueue.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Discarding an unreadable media job {MessageId}.", delivery.BasicProperties.MessageId);
            job = null;
        }

        if (job is null)
        {
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
            return;
        }

        // Quorum queues count earlier deliveries in x-delivery-count (absent on the first).
        var attempt = DeliveryCount(delivery.BasicProperties) + 1;
        var isLastAttempt = attempt >= MediaQueueTopology.MaxDeliveries;

        ProcessingOutcome outcome;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<MediaProcessor>();
            outcome = await processor.ProcessAsync(job, isLastAttempt, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: hand the job back so another worker (or this one, restarted) picks it up.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media job {MediaId} failed unexpectedly (attempt {Attempt}).", job.MediaId, attempt);
            outcome = ProcessingOutcome.Retry;
        }

        if (outcome == ProcessingOutcome.Retry)
        {
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
        else
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
    }

    internal static long DeliveryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue("x-delivery-count", out var value) == true
            ? value switch
            {
                long l => l,
                int i => i,
                byte[] bytes when long.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => 0,
            }
            : 0;
}
