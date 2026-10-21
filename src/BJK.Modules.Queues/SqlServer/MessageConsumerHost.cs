using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Activation;
using Rebus.Backoff;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Exceptions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Retry.Simple;
using Rebus.SqlServer;
using Rebus.Threading;
using Rebus.Time;

namespace BJK.Modules.Queues.SqlServer;

/// <summary>A queue a host reads, registered by <see cref="MessagingServiceCollectionExtensions.AddMessageConsumer{TConsumer}"/>.</summary>
/// <param name="Definition">The queue and how to read it.</param>
/// <param name="ConsumerType">The <see cref="IMessageConsumer"/> resolved for each message.</param>
internal sealed record MessageConsumerRegistration(MessageConsumerDefinition Definition, Type ConsumerType);

/// <summary>
/// Starts one Rebus bus per registered queue, each reading its queue table with <see cref="SqlServerQueueTransport"/>,
/// subscribing it to its topics, and passing every message to its <see cref="IMessageConsumer"/> in a new scope. If the
/// database isn't reachable yet, it keeps trying in the background.
/// </summary>
internal sealed class MessageConsumerHost(
    IEnumerable<MessageConsumerRegistration> registrations,
    MessagingDatabase database,
    IOptions<MessagingOptions> options,
    IServiceScopeFactory scopes,
    ILoggerFactory loggers)
    : BackgroundService
{
    private static readonly TimeSpan StartRetryDelay = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggers.CreateLogger<MessageConsumerHost>();
    private readonly List<BuiltinHandlerActivator> _buses = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = registrations.GroupBy(r => r.Definition.Queue, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        while (pending.Count > 0 && !stoppingToken.IsCancellationRequested)
        {
            foreach (var registration in pending.ToList())
            {
                try
                {
                    await StartBusAsync(registration, stoppingToken);
                    pending.Remove(registration);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Couldn't start reading the '{Queue}' queue; trying again in {Delay}.", registration.Definition.Queue, StartRetryDelay);
                }
            }

            if (pending.Count > 0)
            {
                await Task.Delay(StartRetryDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task StartBusAsync(MessageConsumerRegistration registration, CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var definition = registration.Definition;
        var address = database.Address(definition.Queue);
        var activator = new BuiltinHandlerActivator();
        activator.Handle<RawMessage>((bus, context, body) => DispatchAsync(registration, bus, context, body, stoppingToken));

        try
        {
            var bus = Configure.With(activator)
                .Logging(l => l.Use(new RebusLoggerFactory(loggers)))
                .Transport(t => t.Register(c =>
                {
                    var transport = new SqlServerQueueTransport(
                        new DbConnectionProvider(database.ConnectionString, c.Get<IRebusLoggerFactory>()),
                        address,
                        c.Get<IRebusLoggerFactory>(),
                        c.Get<IAsyncTaskFactory>(),
                        c.Get<IRebusTime>(),
                        QueueTimings.From(settings),
                        settings.CreateTables);
                    if (settings.CreateTables)
                    {
                        transport.EnsureTableIsCreated();
                    }

                    return transport;
                }))
                .Subscriptions(s => s.StoreInSqlServer(
                    database.ConnectionString,
                    database.SubscriptionsTable,
                    isCentralized: true,
                    automaticallyCreateTables: settings.CreateTables))
                .Serialization(s => s.Register(_ => new RawMessageSerializer()))
                .Options(o =>
                {
                    o.SetBusName(definition.Queue);
                    o.SetNumberOfWorkers(definition.Concurrency);
                    o.SetMaxParallelism(definition.Concurrency);
                    o.SetBackoffTimes(settings.PollingInterval);

                    // Our dispatcher decides when to give up (see DispatchAsync); Rebus's own limit is one more, a
                    // safety net in case giving up itself keeps failing.
                    o.RetryStrategy(
                        errorQueueName: database.FailedAddress(definition.Queue),
                        maxDeliveryAttempts: definition.MaxAttempts + 1,
                        errorDetailsHeaderMaxLength: 4000);
                })
                .Start();

            foreach (var topic in definition.Topics)
            {
                await bus.Advanced.Topics.Subscribe(topic);
            }

            lock (_buses)
            {
                _buses.Add(activator);
            }

            _logger.LogInformation(
                "Reading the '{Queue}' queue ({Concurrency} at a time, {MaxAttempts} attempts){Topics}.",
                definition.Queue,
                definition.Concurrency,
                definition.MaxAttempts,
                definition.Topics.Count > 0 ? ", subscribed to " + string.Join(", ", definition.Topics) : string.Empty);
        }
        catch
        {
            activator.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs one delivery through the consumer and turns its outcome into Rebus's: return (done), throw (retry after a
    /// delay), or dead-letter. Gives up after <see cref="MessageConsumerDefinition.MaxAttempts"/>, calling
    /// <see cref="IMessageConsumer.OnGivenUpAsync"/> once.
    /// </summary>
    private async Task DispatchAsync(
        MessageConsumerRegistration registration,
        IBus bus,
        IMessageContext context,
        RawMessage body,
        CancellationToken stoppingToken)
    {
        var definition = registration.Definition;
        var headers = context.Headers;
        var attempt = headers.TryGetValue(Headers.DeliveryCount, out var count) && int.TryParse(count, out var parsed) ? parsed : 1;
        var id = headers.TryGetValue(Headers.MessageId, out var messageId) ? messageId : string.Empty;
        var message = new ReceivedMessage(id, body.Type, body.Json, attempt, definition.MaxAttempts);

        await using var scope = scopes.CreateAsyncScope();
        var consumer = (IMessageConsumer)scope.ServiceProvider.GetRequiredService(registration.ConsumerType);

        if (attempt > definition.MaxAttempts)
        {
            // The last attempt never reported back: the worker crashed or was killed while handling it.
            _logger.LogError(
                "Giving up on message {MessageId} ({Type}) from '{Queue}': all {MaxAttempts} attempts were used, the last without a result.",
                id, body.Type, definition.Queue, definition.MaxAttempts);
            await consumer.OnGivenUpAsync(message, stoppingToken);
            await bus.Advanced.TransportMessage.Deadletter(
                $"Gave up after {definition.MaxAttempts} attempts; the last ended without a result (the worker stopped or crashed).");
            return;
        }

        MessageOutcome outcome;
        Exception? error = null;
        try
        {
            outcome = await consumer.HandleAsync(message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: hand it back at once, without using up an attempt.
            context.TransactionContext.Items[SqlServerQueueTransport.HandBackKey] = true;
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            outcome = MessageOutcome.Retry;
        }

        switch (outcome)
        {
            case MessageOutcome.Completed:
                return;

            case MessageOutcome.DeadLetter:
                _logger.LogWarning(error, "Message {MessageId} ({Type}) from '{Queue}' can't be handled; moving it to the dead-letter table.", id, body.Type, definition.Queue);
                await DeadLetterAsync(bus, error, $"The consumer couldn't handle this {body.Type} message.");
                return;

            case MessageOutcome.Retry when message.IsLastAttempt:
                _logger.LogError(error, "Message {MessageId} ({Type}) from '{Queue}' failed on its last attempt ({Attempt}); moving it to the dead-letter table.", id, body.Type, definition.Queue, attempt);
                await consumer.OnGivenUpAsync(message, stoppingToken);
                await DeadLetterAsync(bus, error, $"Still failing after {attempt} attempts.");
                return;

            default:
                _logger.LogWarning(error, "Message {MessageId} ({Type}) from '{Queue}' failed (attempt {Attempt} of {MaxAttempts}); it will be retried.", id, body.Type, definition.Queue, attempt, definition.MaxAttempts);
                if (error is not null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                throw new RetryRequestedException($"The consumer asked for {body.Type} message {id} to be retried.");
        }
    }

    private static Task DeadLetterAsync(IBus bus, Exception? error, string reason) =>
        error is null
            ? bus.Advanced.TransportMessage.Deadletter(reason)
            : bus.Advanced.TransportMessage.Deadletter(error);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancels the consumers' token first, so work in progress stops and is handed back, then stops the buses.
        await base.StopAsync(cancellationToken);
        List<BuiltinHandlerActivator> buses;
        lock (_buses)
        {
            buses = [.. _buses];
            _buses.Clear();
        }

        foreach (var bus in buses)
        {
            bus.Dispose();
        }
    }
}

/// <summary>Thrown to make Rebus retry a message whose consumer returned <see cref="MessageOutcome.Retry"/>.</summary>
/// <param name="message">What happened.</param>
public sealed class RetryRequestedException(string message) : Exception(message);
