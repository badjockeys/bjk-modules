using System.Collections.Concurrent;
using System.Diagnostics;
using BJK.Modules.Shared.Messaging;
using BJK.Modules.Shared.Messaging.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rebus.SqlServer;
using Rebus.Threading.TaskParallelLibrary;
using Rebus.Time;
using Rebus.Transport;
using Xunit;

namespace BJK.Modules.Shared.Tests.Messaging;

/// <summary>
/// The SQL Server queues against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to a connection string
/// without a database name to run locally). Each test class run uses a new database, dropped afterwards, and each test
/// its own queues.
/// </summary>
public sealed class SqlServerMessagingTests : IAsyncLifetime
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly string _database = "bjk_messaging_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly List<ServiceProvider> _hosts = [];
    private string? _connectionString;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConnectionString => _connectionString ?? throw SkipException();

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("BJK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        await using (var master = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString))
        {
            await master.OpenAsync(Ct);
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_database}]";
            await create.ExecuteNonQueryAsync(Ct);
        }

        _connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = _database }.ConnectionString;
        await ExecuteAsync("CREATE TABLE dbo.Things (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL)");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await StopAsync(host);
        }

        if (_connectionString is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        var server = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var master = new SqlConnection(server);
        await master.OpenAsync(CancellationToken.None);
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_message_sent_with_a_change_is_saved_with_it_and_rolled_back_with_it()
    {
        var queue = NewQueue();
        var host = await StartHostAsync(_ => { });
        var sender = host.GetRequiredService<IMessageSender>();

        // Committed: the row and the message are both there.
        var first = Guid.NewGuid();
        await using (var db = Things())
        {
            db.Things.Add(new Thing { Id = first, Name = "first" });
            await db.SaveChangesWithMessagesAsync(ct => sender.SendAsync(queue, Message("one"), ct), Ct);
        }

        Assert.Equal(1, await CountAsync("dbo.Things"));
        Assert.Equal(1, await CountAsync(Table(queue)));

        // The save fails after the message was written (a duplicate key): neither is kept.
        await using (var db = Things())
        {
            db.Things.Add(new Thing { Id = first, Name = "duplicate" });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => db.SaveChangesWithMessagesAsync(ct => sender.SendAsync(queue, Message("two"), ct), Ct));
        }

        // The work after sending fails: the message isn't kept either.
        await using (var db = Things())
        {
            db.Things.Add(new Thing { Id = Guid.NewGuid(), Name = "third" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesWithMessagesAsync(
                async ct =>
                {
                    await sender.SendAsync(queue, Message("three"), ct);
                    throw new InvalidOperationException("something went wrong after queueing");
                },
                Ct));
        }

        Assert.Equal(1, await CountAsync("dbo.Things"));
        Assert.Equal(1, await CountAsync(Table(queue)));
    }

    [Fact]
    public async Task Competing_consumers_handle_each_message_exactly_once()
    {
        var queue = NewQueue();
        var sender = (await StartHostAsync(_ => { })).GetRequiredService<IMessageSender>();
        var ids = Enumerable.Range(0, 30).Select(i => $"job-{i}").ToList();
        foreach (var id in ids)
        {
            await sender.SendAsync(queue, Message(id), Ct);
        }

        var probes = new List<Probe>();
        for (var i = 0; i < 3; i++)
        {
            var probe = new Probe
            {
                Behaviour = async (_, ct) =>
                {
                    await Task.Delay(50, ct);
                    return MessageOutcome.Completed;
                },
            };
            probes.Add(probe);
            await StartConsumerAsync(queue, probe);
        }

        await EventuallyAsync(() => probes.Sum(p => p.Received.Count) >= ids.Count, "all 30 messages handled");
        await EventuallyAsync(async () => await CountAsync(Table(queue)) == 0, "the queue table emptied");
        await Task.Delay(500, Ct);

        var handled = probes.SelectMany(p => p.Received).Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, handled.Count);
        Assert.Equal(ids.Order(), handled.Order());
        Assert.All(probes.SelectMany(p => p.Received), m => Assert.Equal(1, m.Attempt));
    }

    [Fact]
    public async Task A_failing_message_is_retried_after_a_growing_delay()
    {
        var queue = NewQueue();
        var attempts = new ConcurrentQueue<(int Attempt, TimeSpan At)>();
        var clock = Stopwatch.StartNew();
        var probe = new Probe
        {
            Behaviour = (message, _) =>
            {
                attempts.Enqueue((message.Attempt, clock.Elapsed));
                return message.Attempt < 3
                    ? throw new TimeoutException("temporary")
                    : Task.FromResult(MessageOutcome.Completed);
            },
        };
        var host = await StartConsumerAsync(queue, probe, maxAttempts: 5);

        await host.GetRequiredService<IMessageSender>().SendAsync(queue, Message("retry-me"), Ct);

        await EventuallyAsync(() => attempts.Count >= 3, "three attempts");
        await EventuallyAsync(async () => await CountAsync(Table(queue)) == 0, "the message completed");
        var seen = attempts.ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, seen.Select(a => a.Attempt));
        Assert.True(seen[1].At - seen[0].At >= RetryDelay * 0.8, $"The first retry came after {seen[1].At - seen[0].At}.");
        Assert.True(seen[2].At - seen[1].At >= RetryDelay * 2 * 0.8, $"The second retry came after {seen[2].At - seen[1].At}.");
        Assert.Empty(probe.GivenUp);
        Assert.Equal(0, await CountAsync(FailedTable(queue)));
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_after_its_last_attempt()
    {
        var queue = NewQueue();
        var probe = new Probe { Behaviour = (_, _) => throw new InvalidOperationException("always broken") };
        var host = await StartConsumerAsync(queue, probe, maxAttempts: 3);

        await host.GetRequiredService<IMessageSender>().SendAsync(queue, Message("poison"), Ct);

        // The move to the dead-letter table and the removal from the queue aren't seen at the same instant, so wait for both.
        await EventuallyAsync(
            async () => await CountAsync(FailedTable(queue)) == 1 && await CountAsync(Table(queue)) == 0,
            "the message in the dead-letter table and gone from the queue");
        Assert.Equal(new[] { 1, 2, 3 }, probe.Received.Select(m => m.Attempt));
        var givenUp = Assert.Single(probe.GivenUp);
        Assert.Equal("poison", givenUp.Id);
        Assert.True(givenUp.IsLastAttempt);
    }

    [Fact]
    public async Task A_consumer_can_dead_letter_a_message_it_can_never_handle()
    {
        var queue = NewQueue();
        var probe = new Probe { Behaviour = (_, _) => Task.FromResult(MessageOutcome.DeadLetter) };
        var host = await StartConsumerAsync(queue, probe, maxAttempts: 5);

        await host.GetRequiredService<IMessageSender>().SendAsync(queue, Message("unreadable"), Ct);

        await EventuallyAsync(async () => await CountAsync(FailedTable(queue)) == 1, "the message in the dead-letter table");
        Assert.Single(probe.Received);
        Assert.Empty(probe.GivenUp);
    }

    [Fact]
    public async Task A_published_event_is_copied_to_every_subscribed_queue()
    {
        var topic = "test.topic." + Guid.NewGuid().ToString("N")[..8];
        var first = new Probe();
        var second = new Probe();
        await StartConsumerAsync(NewQueue(), first, topics: [topic]);
        var host = await StartConsumerAsync(NewQueue(), second, topics: [topic]);
        var sender = host.GetRequiredService<IMessageSender>();
        await EventuallyAsync(async () => await sender.CountSubscribersAsync(topic, Ct) == 2, "both queues subscribed");

        var copies = await sender.PublishAsync(topic, Message("event-1"), Ct);

        Assert.Equal(2, copies);
        await EventuallyAsync(() => !first.Received.IsEmpty && !second.Received.IsEmpty, "both queues received the event");
        Assert.Equal("event-1", Assert.Single(first.Received).Id);
        Assert.Equal("event-1", Assert.Single(second.Received).Id);
        Assert.Equal("{\"name\":\"event-1\"}", first.Received.Single().Payload);
    }

    [Fact]
    public async Task Publishing_to_a_topic_nobody_subscribes_to_sends_nothing()
    {
        var sender = (await StartHostAsync(_ => { })).GetRequiredService<IMessageSender>();

        Assert.Equal(0, await sender.PublishAsync("test.nobody." + Guid.NewGuid().ToString("N")[..8], Message("lost?"), Ct));
    }

    [Fact]
    public async Task A_message_whose_worker_crashed_is_delivered_again_when_its_lease_ends()
    {
        var queue = NewQueue();
        var sender = (await StartHostAsync(_ => { })).GetRequiredService<IMessageSender>();
        await sender.SendAsync(queue, Message("crash-me"), Ct);

        // A worker claims the message and dies: no acknowledgement, no release, no lease renewal.
        var lease = TimeSpan.FromSeconds(3);
        var crashed = Transport(queue, new QueueTimings(lease, RenewEvery: null, RetryDelay, TimeSpan.FromSeconds(2)));
        var claimed = await crashed.Receive(new AbandonedTransaction(), Ct);
        Assert.NotNull(claimed);
        var clock = Stopwatch.StartNew();

        var probe = new Probe();
        await StartConsumerAsync(queue, probe);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Empty(probe.Received);

        await EventuallyAsync(() => !probe.Received.IsEmpty, "the message delivered again");
        Assert.True(clock.Elapsed >= lease * 0.8, $"Delivered again after {clock.Elapsed}, before the lease ended.");
        var redelivered = Assert.Single(probe.Received);
        Assert.Equal(2, redelivered.Attempt);
        Assert.True(redelivered.IsRedelivery);
        await EventuallyAsync(async () => await CountAsync(Table(queue)) == 0, "the message completed");
    }

    [Fact]
    public async Task A_message_whose_last_attempt_crashed_is_given_up_without_running_it_again()
    {
        var queue = NewQueue();
        var sender = (await StartHostAsync(_ => { })).GetRequiredService<IMessageSender>();
        await sender.SendAsync(queue, Message("crashes-every-time"), Ct);

        // Three claims that never finished (e.g. the worker ran out of memory each time); the last lease has ended.
        await ExecuteAsync($"UPDATE {Table(queue)} SET [deliverycount] = 3");

        var probe = new Probe();
        await StartConsumerAsync(queue, probe, maxAttempts: 3);

        await EventuallyAsync(async () => await CountAsync(FailedTable(queue)) == 1, "the message in the dead-letter table");
        Assert.Empty(probe.Received);
        var givenUp = Assert.Single(probe.GivenUp);
        Assert.Equal(4, givenUp.Attempt);
    }

    [Fact]
    public async Task A_delayed_message_stays_hidden_until_its_time()
    {
        var queue = NewQueue();
        var probe = new Probe();
        var host = await StartConsumerAsync(queue, probe);
        var clock = Stopwatch.StartNew();

        await host.GetRequiredService<IMessageSender>().SendAsync(queue, Message("later") with { Delay = TimeSpan.FromSeconds(3) }, Ct);

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Empty(probe.Received);
        await EventuallyAsync(() => !probe.Received.IsEmpty, "the delayed message delivered");
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.5), $"Delivered after {clock.Elapsed}.");
    }

    [Fact]
    public void Retry_delays_double_up_to_the_maximum()
    {
        var timings = new QueueTimings(TimeSpan.FromMinutes(1), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(5), timings.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(10), timings.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(20), timings.DelayAfter(3));
        Assert.Equal(TimeSpan.FromSeconds(30), timings.DelayAfter(4));
        Assert.Equal(TimeSpan.FromSeconds(30), timings.DelayAfter(100));
    }

    // ---------- helpers ----------

    private static string NewQueue() => "test-" + Guid.NewGuid().ToString("N")[..10];

    private static string Table(string queue) => $"[messaging].[{queue}]";

    private static string FailedTable(string queue) => $"[messaging].[{queue}.failed]";

    private static OutgoingMessage Message(string id) => OutgoingMessage.Create(id, "TestMessage", new { name = id });

    private static Exception SkipException()
    {
        Assert.Skip("BJK_TEST_SQLSERVER isn't set.");
        return new InvalidOperationException();
    }

    private ThingsDbContext Things() => new(new DbContextOptionsBuilder<ThingsDbContext>()
        .UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure())
        .Options);

    private SqlServerQueueTransport Transport(string queue, QueueTimings timings)
    {
        var logging = new Rebus.Logging.NullLoggerFactory();
        return new SqlServerQueueTransport(
            new DbConnectionProvider(ConnectionString, logging),
            Table(queue),
            logging,
            new TplAsyncTaskFactory(logging),
            new DefaultRebusTime(),
            timings,
            createTables: true);
    }

    private Task<ServiceProvider> StartConsumerAsync(string queue, Probe probe, int maxAttempts = 5, IReadOnlyList<string>? topics = null) =>
        StartHostAsync((services, configuration) =>
        {
            services.AddSingleton(probe);
            services.AddMessageConsumer<ProbeConsumer>(
                configuration,
                "Test",
                new MessageConsumerDefinition(queue) { MaxAttempts = maxAttempts, Topics = topics ?? [] });
        });

    private Task<ServiceProvider> StartHostAsync(Action<IServiceCollection> register) =>
        StartHostAsync((services, _) => register(services));

    private async Task<ServiceProvider> StartHostAsync(Action<IServiceCollection, IConfiguration> register)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Test"] = ConnectionString,
                ["Messaging:PollingInterval"] = "00:00:00.100",
                ["Messaging:RetryDelay"] = RetryDelay.ToString(),
                ["Messaging:MaxRetryDelay"] = "00:00:05",
                ["Messaging:LeaseDuration"] = "00:00:30",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new TestOutputLoggerProvider()).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBjkMessaging(configuration, "Test");
        register(services, configuration);

        var provider = services.BuildServiceProvider();
        _hosts.Add(provider);
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(Ct);
        }

        return provider;
    }

    private static async Task StopAsync(ServiceProvider host)
    {
        foreach (var hosted in host.GetServices<IHostedService>())
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        await host.DisposeAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<int> CountAsync(string table)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF OBJECT_ID('{table.Replace("'", "''", StringComparison.Ordinal)}') IS NULL SELECT 0 ELSE SELECT COUNT(*) FROM {table}";
        return (int)(await command.ExecuteScalarAsync(Ct) ?? 0);
    }

    private static Task EventuallyAsync(Func<bool> condition, string what) =>
        EventuallyAsync(() => Task.FromResult(condition()), what);

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}

/// <summary>What the test consumers saw, and what they should do.</summary>
public sealed class Probe
{
    public ConcurrentQueue<ReceivedMessage> Received { get; } = new();

    public ConcurrentQueue<ReceivedMessage> GivenUp { get; } = new();

    public Func<ReceivedMessage, CancellationToken, Task<MessageOutcome>> Behaviour { get; init; } =
        (_, _) => Task.FromResult(MessageOutcome.Completed);
}

internal sealed class ProbeConsumer(Probe probe) : IMessageConsumer
{
    public Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        probe.Received.Enqueue(message);
        return probe.Behaviour(message, cancellationToken);
    }

    public Task OnGivenUpAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        probe.GivenUp.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>A Rebus transaction that never completes: what a worker that crashed mid-message leaves behind.</summary>
internal sealed class AbandonedTransaction : ITransactionContext
{
    public ConcurrentDictionary<string, object> Items { get; } = new();

    public void OnCommit(Func<ITransactionContext, Task> commitAction)
    {
    }

    public void OnRollback(Func<ITransactionContext, Task> abortedAction)
    {
    }

    public void OnAck(Func<ITransactionContext, Task> completedAction)
    {
    }

    public void OnNack(Func<ITransactionContext, Task> commitAction)
    {
    }

    public void OnDisposed(Action<ITransactionContext> disposedAction)
    {
    }

    public void SetResult(bool commit, bool ack)
    {
    }

    public void Dispose()
    {
    }
}

public sealed class Thing
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class ThingsDbContext(DbContextOptions<ThingsDbContext> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();
}

/// <summary>Writes warnings and errors (Rebus's included) to the test's output, to explain failures.</summary>
internal sealed class TestOutputLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.GetType().Name + ": " + exception.Message)}");
            }
            catch (InvalidOperationException)
            {
                // No test is running (a background thread after the test ended).
            }
        }
    }
}
