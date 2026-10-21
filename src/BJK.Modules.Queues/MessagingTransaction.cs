using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BJK.Modules.Queues;

/// <summary>
/// Queues messages in the same database transaction as a data change (the transactional outbox, without a separate
/// outbox table): <see cref="SaveChangesWithMessagesAsync"/> saves a DbContext's changes and writes the messages sent
/// meanwhile through <see cref="IMessageSender"/> into their queue tables in one transaction. If anything fails,
/// neither the change nor the messages are saved.
/// </summary>
/// <remarks>
/// Works when the queue tables are in the same database as the DbContext (bjk-core). If they're elsewhere (the
/// identity server's queues are in the app database), messages are sent in their own transaction instead, straight
/// away; see <see cref="OutboxPublisher{TContext}"/> for how the identity server stays reliable.
/// </remarks>
public static class MessagingTransaction
{
    private static readonly AsyncLocal<EnlistedTransaction?> CurrentTransaction = new();

    /// <summary>The DbContext transaction that messages sent now should join, if any.</summary>
    internal static EnlistedTransaction? Current => CurrentTransaction.Value;

    /// <summary>
    /// Runs <paramref name="sendMessages"/> and saves <paramref name="db"/>'s changes in one database transaction.
    /// Uses the context's execution strategy, so a transient failure retries the whole unit (the messages too).
    /// </summary>
    /// <param name="db">The module's DbContext, with its changes made.</param>
    /// <param name="sendMessages">Sends the messages through <see cref="IMessageSender"/> (e.g. queues a job).</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    public static async Task SaveChangesWithMessagesAsync(
        this DbContext db,
        Func<CancellationToken, Task> sendMessages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sendMessages);

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var previous = CurrentTransaction.Value;
            CurrentTransaction.Value = new EnlistedTransaction(
                (SqlConnection)db.Database.GetDbConnection(),
                (SqlTransaction)transaction.GetDbTransaction());
            try
            {
                await sendMessages(cancellationToken);
            }
            finally
            {
                CurrentTransaction.Value = previous;
            }

            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        db.ChangeTracker.AcceptAllChanges();
    }
}

/// <summary>An open DbContext transaction that messages can be written in.</summary>
/// <param name="Connection">The DbContext's connection.</param>
/// <param name="Transaction">Its current transaction.</param>
internal sealed record EnlistedTransaction(SqlConnection Connection, SqlTransaction Transaction);
