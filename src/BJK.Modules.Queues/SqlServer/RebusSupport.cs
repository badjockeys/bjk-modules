using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Serialization;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BJK.Modules.Queues.SqlServer;

/// <summary>
/// A message body as Rebus carries it for us: the type name and the JSON, untouched. Our code (de)serialises the JSON
/// itself, so the rows stay readable and nothing depends on .NET type names.
/// </summary>
/// <param name="Type">The type name (Rebus's <c>rbs2-msg-type</c> header).</param>
/// <param name="Json">The content.</param>
public sealed record RawMessage(string Type, string Json);

/// <summary>Stores <see cref="RawMessage"/> bodies as UTF-8 JSON.</summary>
internal sealed class RawMessageSerializer : ISerializer
{
    public const string ContentType = "application/json;charset=utf-8";

    public Task<TransportMessage> Serialize(Message message)
    {
        if (message.Body is not RawMessage raw)
        {
            throw new InvalidOperationException($"Only {nameof(RawMessage)} bodies can be sent, not {message.Body?.GetType().Name ?? "null"}.");
        }

        var headers = new Dictionary<string, string>(message.Headers)
        {
            [Headers.Type] = raw.Type,
            [Headers.ContentType] = ContentType,
        };
        return Task.FromResult(new TransportMessage(headers, Encoding.UTF8.GetBytes(raw.Json)));
    }

    public Task<Message> Deserialize(TransportMessage transportMessage)
    {
        var headers = new Dictionary<string, string>(transportMessage.Headers);
        var type = headers.TryGetValue(Headers.Type, out var value) ? value : string.Empty;
        return Task.FromResult(new Message(headers, new RawMessage(type, Encoding.UTF8.GetString(transportMessage.Body))));
    }
}

/// <summary>Sends Rebus's own logging to the host's <see cref="ILoggerFactory"/>.</summary>
internal sealed class RebusLoggerFactory(ILoggerFactory loggers) : AbstractRebusLoggerFactory
{
    protected override ILog GetLogger(Type type) => new Log(loggers.CreateLogger(type));

    private sealed class Log(ILogger logger) : ILog
    {
#pragma warning disable CA2254 // Rebus's messages are templates with positional placeholders, like ours.
        public void Debug(string message, params object[] objs) => logger.Log(MsLogLevel.Debug, message, objs);

        public void Info(string message, params object[] objs) => logger.Log(MsLogLevel.Information, message, objs);

        public void Warn(string message, params object[] objs) => logger.Log(MsLogLevel.Warning, message, objs);

        public void Warn(Exception exception, string message, params object[] objs) => logger.Log(MsLogLevel.Warning, exception, message, objs);

        public void Error(string message, params object[] objs) => logger.Log(MsLogLevel.Error, message, objs);

        public void Error(Exception exception, string message, params object[] objs) => logger.Log(MsLogLevel.Error, exception, message, objs);
#pragma warning restore CA2254
    }
}

/// <summary>Where the queue tables are, and how queue names map to them.</summary>
/// <param name="ConnectionString">The database holding the queue tables.</param>
/// <param name="Schema">Their schema.</param>
internal sealed record MessagingDatabase(string ConnectionString, string Schema)
{
    /// <summary>The table Rebus keeps topic subscriptions in.</summary>
    public string SubscriptionsTable => $"[{Schema}].[subscriptions]";

    /// <summary>A queue's address, which is its table: <c>[messaging].[media.process]</c>.</summary>
    /// <param name="queue">The queue name.</param>
    public string Address(string queue) => $"[{Schema}].[{queue}]";

    /// <summary>The address of a queue's dead-letter table, <c>[messaging].[media.process.failed]</c>.</summary>
    /// <param name="queue">The queue name.</param>
    public string FailedAddress(string queue) => Address(queue + ".failed");

    /// <summary>True if <paramref name="connection"/> is to this same database (so a transaction on it can include the queues).</summary>
    /// <param name="connection">An open connection.</param>
    public bool IsSameDatabase(SqlConnection connection)
    {
        var ours = new SqlConnectionStringBuilder(ConnectionString);
        return string.Equals(Normalise(ours.DataSource), Normalise(connection.DataSource), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ours.InitialCatalog, connection.Database, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string dataSource) =>
        dataSource.Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty, StringComparison.Ordinal);
}
