using System.Data;
using Microsoft.Data.SqlClient;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.SqlServer;
using Rebus.SqlServer.Transport;
using Rebus.Threading;
using Rebus.Time;
using Rebus.Transport;
using IDbConnection = Rebus.SqlServer.IDbConnection;

namespace BJK.Modules.Shared.Messaging.SqlServer;

/// <summary>Lease and retry timings for <see cref="SqlServerQueueTransport"/>.</summary>
/// <param name="Lease">How long a received message stays hidden from other consumers while it's being worked on.</param>
/// <param name="RenewEvery">How often the lease is extended while the work goes on; null never extends it.</param>
/// <param name="RetryDelay">The wait before the second attempt; doubled for each further attempt.</param>
/// <param name="MaxRetryDelay">The longest wait between attempts.</param>
internal sealed record QueueTimings(TimeSpan Lease, TimeSpan? RenewEvery, TimeSpan RetryDelay, TimeSpan MaxRetryDelay)
{
    public static QueueTimings From(MessagingOptions options) =>
        new(options.LeaseDuration, options.LeaseDuration / 3, options.RetryDelay, options.MaxRetryDelay);

    /// <summary>The wait after failed attempt number <paramref name="attempt"/> (1 = the first).</summary>
    /// <param name="attempt">The attempt that failed.</param>
    public TimeSpan DelayAfter(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var delay = TimeSpan.FromMilliseconds(Math.Min(RetryDelay.TotalMilliseconds * factor, MaxRetryDelay.TotalMilliseconds));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}

/// <summary>
/// Rebus's SQL Server lease transport, plus the two things our queues need that it doesn't keep: a delivery count
/// stored with each message, and a delay before a failed message is tried again.
/// </summary>
/// <remarks>
/// <para>
/// Each queue is a table. A consumer claims the next visible message with <c>UPDLOCK, READPAST, ROWLOCK</c> (competing
/// consumers skip rows another one has locked) and leases it: it stays in the table, hidden until the lease ends, and
/// the lease is renewed while the work goes on. Done: the row is deleted. Failed: the lease is released and the row is
/// hidden for <see cref="QueueTimings.DelayAfter"/>. A crashed or killed worker simply stops renewing, so the message
/// comes back when its lease runs out.
/// </para>
/// <para>
/// <c>deliverycount</c> goes up each time a message is claimed (crashes included, like RabbitMQ's
/// <c>x-delivery-count</c>) and is handed to Rebus in its standard <c>rbs2-delivery-count</c> header. A worker that
/// stops cleanly hands its message back without using up an attempt.
/// </para>
/// <para>
/// Sending inside <see cref="MessagingTransaction.SaveChangesWithMessagesAsync"/> writes the row with the DbContext's
/// own connection and transaction; otherwise rows are written when the Rebus transaction commits.
/// </para>
/// </remarks>
internal sealed class SqlServerQueueTransport : SqlServerLeaseTransport
{
    /// <summary>Transaction context item: the DbContext connection (a <see cref="IDbConnection"/>) sends must use.</summary>
    public const string EnlistedConnectionKey = "bjk-enlisted-connection";

    /// <summary>Transaction context item: hand the message back without counting the attempt or waiting (shutdown).</summary>
    public const string HandBackKey = "bjk-hand-back";

    private readonly QueueTimings _timings;
    private readonly Func<string> _leasedBy;

    public SqlServerQueueTransport(
        IDbConnectionProvider connectionProvider,
        string? inputQueueAddress,
        IRebusLoggerFactory rebusLoggerFactory,
        IAsyncTaskFactory asyncTaskFactory,
        IRebusTime rebusTime,
        QueueTimings timings,
        bool createTables)
        : base(
            connectionProvider,
            inputQueueAddress,
            rebusLoggerFactory,
            asyncTaskFactory,
            rebusTime,
            timings.Lease,
            TimeSpan.Zero,
            LeasedBy,
            new SqlServerLeaseTransportOptions(connectionProvider).SetEnsureTablesAreCreated(createTables))
    {
        _timings = timings;
        _leasedBy = LeasedBy;
    }

    private static string LeasedBy() => $"{Environment.MachineName}/{Environment.ProcessId}";

    public override Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
    {
        if (context.Items.TryGetValue(EnlistedConnectionKey, out var value) && value is IDbConnection connection)
        {
            return InnerSendAsync(GetDestinationAddressToUse(destinationAddress, message), message, connection);
        }

        return base.Send(destinationAddress, message, context);
    }

    protected override async Task<TransportMessage> ReceiveInternal(ITransactionContext context, CancellationToken cancellationToken)
    {
        TransportMessage? message;
        long id = 0;
        int deliveryCount = 0;

        using (var connection = await ConnectionProvider.GetConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandType = CommandType.Text;
            command.CommandText = $"""
                ;WITH next AS (
                    SELECT TOP 1 [id], [headers], [body], [leasedat], [leaseduntil], [leasedby], [deliverycount]
                    FROM {Qualified(ReceiveTableName)} WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [visible] < SYSDATETIMEOFFSET()
                      AND [expiration] > SYSDATETIMEOFFSET()
                      AND ([leaseduntil] IS NULL OR [leaseduntil] < SYSDATETIMEOFFSET())
                    ORDER BY [priority] DESC, [visible] ASC, [id] ASC)
                UPDATE next
                SET [leaseduntil] = DATEADD(ms, @leasems, SYSDATETIMEOFFSET()),
                    [leasedat] = SYSDATETIMEOFFSET(),
                    [leasedby] = @leasedby,
                    [deliverycount] = [deliverycount] + 1
                OUTPUT inserted.[id], inserted.[headers], inserted.[body], inserted.[deliverycount];
                """;
            command.Parameters.Add("@leasems", SqlDbType.Int).Value = (int)_timings.Lease.TotalMilliseconds;
            command.Parameters.Add("@leasedby", SqlDbType.NVarChar, LeasedByColumnSize).Value = _leasedBy();

            try
            {
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                message = await ExtractTransportMessageFromReader(reader, cancellationToken);
                if (message is not null)
                {
                    id = (long)reader["id"];
                    deliveryCount = (int)reader["deliverycount"];
                }
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Receive operation was cancelled", exception);
            }

            await connection.Complete();
        }

        if (message is null)
        {
            return null!;
        }

        message.Headers[Headers.DeliveryCount] = deliveryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Track(context, id, deliveryCount);
        return message;
    }

    private void Track(ITransactionContext context, long id, int deliveryCount)
    {
        var renewal = _timings.RenewEvery is { } every && every > TimeSpan.Zero
            ? new LeaseRenewal(this, id, every)
            : null;

        context.OnAck(async _ =>
        {
            renewal?.Dispose();
            try
            {
                await DeleteMessage(id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't delete message {id} from {queue}; it will be delivered again when its lease ends", id, Qualified(ReceiveTableName));
            }
        });

        context.OnNack(async ctx =>
        {
            renewal?.Dispose();
            try
            {
                var handBack = ctx.Items.ContainsKey(HandBackKey);
                await ReleaseAsync(id, handBack ? TimeSpan.Zero : _timings.DelayAfter(deliveryCount), handBack);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't release message {id} in {queue}; it will be delivered again when its lease ends", id, Qualified(ReceiveTableName));
            }
        });

        context.OnDisposed(_ => renewal?.Dispose());
    }

    /// <summary>Ends the lease and hides the message for <paramref name="delay"/>; a hand-back doesn't count the attempt.</summary>
    private async Task ReleaseAsync(long id, TimeSpan delay, bool handBack)
    {
        using var connection = await ConnectionProvider.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {Qualified(ReceiveTableName)} WITH (ROWLOCK)
            SET [leaseduntil] = NULL, [leasedby] = NULL, [leasedat] = NULL,
                [visible] = DATEADD(ms, @delayms, SYSDATETIMEOFFSET()),
                [deliverycount] = CASE WHEN @handback = 1 AND [deliverycount] > 0 THEN [deliverycount] - 1 ELSE [deliverycount] END
            WHERE [id] = @id
            """;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@delayms", SqlDbType.Int).Value = (int)Math.Min(delay.TotalMilliseconds, int.MaxValue);
        command.Parameters.Add("@handback", SqlDbType.Bit).Value = handBack;
        await command.ExecuteNonQueryAsync();
        await connection.Complete();
    }

    private async Task RenewAsync(long id)
    {
        using var connection = await ConnectionProvider.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {Qualified(ReceiveTableName)} WITH (ROWLOCK)
            SET [leaseduntil] = DATEADD(ms, @leasems, SYSDATETIMEOFFSET())
            WHERE [id] = @id AND [leaseduntil] IS NOT NULL
            """;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@leasems", SqlDbType.Int).Value = (int)_timings.Lease.TotalMilliseconds;
        await command.ExecuteNonQueryAsync();
        await connection.Complete();
    }

    protected override string AdditionalSchemaModifications(TableName tableName)
    {
        var table = Qualified(tableName);
        return base.AdditionalSchemaModifications(tableName) + $"""

            ----

            IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = '{tableName.Schema}' AND TABLE_NAME = '{tableName.Name}' AND COLUMN_NAME = 'deliverycount')
            BEGIN
                ALTER TABLE {table} ADD [deliverycount] int NOT NULL CONSTRAINT [DF_{tableName.Schema}_{tableName.Name}_deliverycount] DEFAULT 0
            END
            """;
    }

    private static string Qualified(TableName table) => $"[{table.Schema}].[{table.Name}]";

    /// <summary>Extends a message's lease every so often until disposed.</summary>
    private sealed class LeaseRenewal : IDisposable
    {
        private readonly SqlServerQueueTransport _transport;
        private readonly long _id;
        private readonly Timer _timer;
        private int _disposed;

        public LeaseRenewal(SqlServerQueueTransport transport, long id, TimeSpan every)
        {
            _transport = transport;
            _id = id;
            _timer = new Timer(_ => _ = RenewAsync(), null, every, every);
        }

        private async Task RenewAsync()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            try
            {
                await _transport.RenewAsync(_id);
            }
            catch (Exception ex)
            {
                _transport.Log.Warn(ex, "Couldn't renew the lease of message {id}", _id);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _timer.Dispose();
            }
        }
    }
}
