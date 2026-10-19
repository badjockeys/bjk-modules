using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Activation;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.SqlServer;
using Rebus.Threading;
using Rebus.Threading.TaskParallelLibrary;
using Rebus.Time;
using Rebus.Transport;

namespace BJK.Modules.Shared.Messaging.SqlServer;

/// <summary>
/// <see cref="IMessageSender"/> over Rebus's SQL Server transport: one send-only Rebus bus per host, created on first
/// use (so a host whose database isn't reachable yet still starts, and a send just fails and is retried by its caller).
/// </summary>
internal sealed class SqlServerMessageSender(
    MessagingDatabase database,
    IOptions<MessagingOptions> options,
    ILoggerFactory loggers,
    TimeProvider clock)
    : IMessageSender, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _createdQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<SqlServerMessageSender>();
    private BuiltinHandlerActivator? _activator;
    private IBus? _bus;
    private SqlServerQueueTransport? _transport;

    public async Task SendAsync(string queue, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(message);

        var (bus, _) = await GetBusAsync(cancellationToken);
        var address = database.Address(queue);
        if (options.Value.CreateTables && _createdQueues.TryAdd(address, true))
        {
            try
            {
                CreateQueueTable(address);
            }
            catch
            {
                _createdQueues.TryRemove(address, out _);
                throw;
            }
        }

        var headers = HeadersFor(message);
        if (message.Delay is { } delay && delay > TimeSpan.Zero)
        {
            // The SQL transport stores the row as invisible until then (and drops the header).
            headers[Headers.DeferredUntil] = clock.GetUtcNow().Add(delay).ToIso8601DateTimeOffset();
            headers[Headers.DeferredRecipient] = address;
        }

        await InTransactionAsync(() => bus.Advanced.Routing.Send(address, new RawMessage(message.Type, message.Payload), headers));
    }

    public async Task<int> PublishAsync(string topic, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(message);

        var subscribers = await CountSubscribersAsync(topic, cancellationToken);
        if (subscribers == 0)
        {
            return 0;
        }

        var (bus, _) = await GetBusAsync(cancellationToken);
        await InTransactionAsync(() => bus.Advanced.Topics.Publish(topic, new RawMessage(message.Type, message.Payload), HeadersFor(message)));
        return subscribers;
    }

    public async Task<int> CountSubscribersAsync(string topic, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {database.SubscriptionsTable} WHERE [topic] = @topic";
            command.Parameters.Add("@topic", SqlDbType.NVarChar, 200).Value = topic;
            return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060)
        {
            // No subscriptions table (208) or no database (4060) yet: no consumer has started.
            return 0;
        }
    }

    /// <summary>
    /// Creates a queue's table (with the lease and delivery count columns) if it's missing, so a job can be queued
    /// before any worker has started. The send-only bus itself never creates tables (it would add an unused
    /// <c>error</c> queue).
    /// </summary>
    private void CreateQueueTable(string address)
    {
        var rebusLoggers = new RebusLoggerFactory(loggers);
        using var creator = new SqlServerQueueTransport(
            new DbConnectionProvider(database.ConnectionString, rebusLoggers),
            address,
            rebusLoggers,
            new TplAsyncTaskFactory(rebusLoggers),
            new DefaultRebusTime(),
            QueueTimings.From(options.Value),
            createTables: true);
        creator.EnsureTableIsCreated();
    }

    private static Dictionary<string, string> HeadersFor(OutgoingMessage message) => new()
    {
        [Headers.MessageId] = message.Id,
        [Headers.Type] = message.Type,
    };

    /// <summary>
    /// Runs one send in its own Rebus transaction, joined to the current
    /// <see cref="MessagingTransaction.SaveChangesWithMessagesAsync"/> transaction when there is one on this database.
    /// </summary>
    private async Task InTransactionAsync(Func<Task> send)
    {
        using var scope = new RebusTransactionScope();
        if (MessagingTransaction.Current is { } enlisted)
        {
            if (database.IsSameDatabase(enlisted.Connection))
            {
                scope.TransactionContext.Items[SqlServerQueueTransport.EnlistedConnectionKey] =
                    new DbConnectionWrapper(enlisted.Connection, enlisted.Transaction, managedExternally: true);
            }
            else
            {
                _logger.LogDebug("The queues are in another database than the data change, so this message is sent on its own.");
            }
        }

        await send();
        await scope.CompleteAsync();
    }

    private async Task<(IBus Bus, SqlServerQueueTransport Transport)> GetBusAsync(CancellationToken cancellationToken)
    {
        if (_bus is not null && _transport is not null)
        {
            return (_bus, _transport);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_bus is null || _transport is null)
            {
                var settings = options.Value;
                SqlServerQueueTransport? transport = null;
                var activator = new BuiltinHandlerActivator();
                try
                {
                    var bus = Configure.With(activator)
                        .Logging(l => l.Use(new RebusLoggerFactory(loggers)))
                        .Transport(t =>
                        {
                            OneWayClientBackdoor.ConfigureOneWayClient(t);
                            t.Register(c => transport = new SqlServerQueueTransport(
                                new DbConnectionProvider(database.ConnectionString, c.Get<IRebusLoggerFactory>()),
                                inputQueueAddress: null,
                                c.Get<IRebusLoggerFactory>(),
                                c.Get<IAsyncTaskFactory>(),
                                c.Get<IRebusTime>(),
                                QueueTimings.From(settings),
                                createTables: false));
                        })
                        .Subscriptions(s => s.StoreInSqlServer(
                            database.ConnectionString,
                            database.SubscriptionsTable,
                            isCentralized: true,
                            automaticallyCreateTables: settings.CreateTables))
                        .Serialization(s => s.Register(_ => new RawMessageSerializer()))
                        .Start();

                    _activator = activator;
                    _transport = transport ?? throw new InvalidOperationException("The SQL Server transport wasn't created.");
                    _bus = bus;
                }
                catch
                {
                    activator.Dispose();
                    throw;
                }
            }

            return (_bus!, _transport!);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _activator?.Dispose();
        _lock.Dispose();
    }
}
