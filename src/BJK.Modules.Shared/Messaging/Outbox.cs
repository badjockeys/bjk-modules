using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Shared.Messaging;

/// <summary>
/// An event waiting to be published. It's saved in the same transaction as the change it describes,
/// then <see cref="OutboxPublisher{TContext}"/> publishes it to its topic's queues, so an event is never lost
/// (or sent for a change that was rolled back). Stored in the module's own schema, table <c>Outbox</c>.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 200;
    public const int LastErrorMaxLength = 1000;

    /// <summary>How events are serialised: web defaults (camelCase) with enums as names. Consumers read with the same.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // For EF Core.
    private OutboxMessage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The event's type name, sent as the message type.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>The event as JSON.</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage For<TEvent>(Guid id, TEvent payload, DateTimeOffset occurredAt)
        where TEvent : notnull => For(id, typeof(TEvent).Name, payload, occurredAt);

    /// <summary>An event with an explicit message type, such as <c>media.ready</c>.</summary>
    /// <param name="id">The message id (consumers use it to spot repeats).</param>
    /// <param name="type">Sent as the message type.</param>
    /// <param name="payload">The event, serialised as JSON.</param>
    /// <param name="occurredAt">When it happened.</param>
    public static OutboxMessage For<TEvent>(Guid id, string type, TEvent payload, DateTimeOffset occurredAt)
        where TEvent : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new OutboxMessage
        {
            Id = id,
            Type = type.Length <= TypeMaxLength ? type : type[..TypeMaxLength],
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAt = occurredAt,
        };
    }

    internal void MarkPublished(DateTimeOffset now)
    {
        PublishedAt = now;
        Attempts++;
        LastError = null;
    }

    internal void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
    }
}

public static class OutboxModelBuilderExtensions
{
    /// <summary>Adds the <c>Outbox</c> table to a module's DbContext (in its default schema).</summary>
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("Outbox");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).ValueGeneratedNever();
            b.Property(m => m.Type).HasMaxLength(OutboxMessage.TypeMaxLength).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.LastError).HasMaxLength(OutboxMessage.LastErrorMaxLength);

            // The publisher's query: unpublished, oldest first.
            b.HasIndex(m => new { m.PublishedAt, m.OccurredAt });
        });

        return modelBuilder;
    }
}

/// <summary>
/// Publishes a module's outbox to a topic (every subscribed queue gets a copy), oldest first. Runs in every instance
/// of the host. When the queues are in the module's own database (bjk-core), each batch is published and marked as
/// published in one transaction, so it goes out exactly once. When they're in another database (the identity server
/// publishes into bjk-core's queues), an event is published first and marked afterwards, so very occasionally it may
/// go out twice; consumers are idempotent. Events wait while nothing subscribes to the topic yet (bjk-core subscribes
/// when it starts), so none are lost on a fresh environment. Published events are kept for 30 days, then removed.
/// </summary>
public sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes,
    IMessageSender sender,
    string topic,
    TimeProvider clock,
    ILogger<OutboxPublisher<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoSubscriberWarningEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepPublished = TimeSpan.FromDays(30);
    private const int BatchSize = 50;

    private DateTimeOffset _lastNoSubscriberWarning = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await PublishBatchAsync(stoppingToken);
                if (sent < BatchSize)
                {
                    await Task.Delay(IdleDelay, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publishing the {Context} outbox to '{Topic}' failed; retrying shortly.", typeof(TContext).Name, topic);
                await Task.Delay(ErrorDelay, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Publishes up to one batch; returns how many were published.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    internal async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var outbox = db.Set<OutboxMessage>();

        var pending = await outbox
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count > 0)
        {
            if (await sender.CountSubscribersAsync(topic, cancellationToken) == 0)
            {
                WarnNoSubscribers(pending.Count);
                return 0;
            }

            try
            {
                await db.SaveChangesWithMessagesAsync(
                    async ct =>
                    {
                        foreach (var message in pending)
                        {
                            await sender.PublishAsync(topic, new OutgoingMessage(message.Id.ToString("D"), message.Type, message.Payload), ct);
                            message.MarkPublished(clock.GetUtcNow());
                        }
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Record the error on the oldest event, in a fresh context (the failed one may hold half-applied changes).
                await using var errorScope = scopes.CreateAsyncScope();
                var errorDb = errorScope.ServiceProvider.GetRequiredService<TContext>();
                var first = await errorDb.Set<OutboxMessage>().FirstOrDefaultAsync(m => m.Id == pending[0].Id, cancellationToken);
                if (first is not null && first.PublishedAt is null)
                {
                    first.MarkFailed(ex.Message);
                    await errorDb.SaveChangesAsync(cancellationToken);
                }

                throw;
            }
        }

        var cutoff = clock.GetUtcNow() - KeepPublished;
        await outbox.Where(m => m.PublishedAt != null && m.PublishedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

        return pending.Count;
    }

    private void WarnNoSubscribers(int waiting)
    {
        var now = clock.GetUtcNow();
        if (now - _lastNoSubscriberWarning < NoSubscriberWarningEvery)
        {
            return;
        }

        _lastNoSubscriberWarning = now;
        logger.LogWarning(
            "No queue subscribes to '{Topic}' yet, so {Count} {Context} outbox event(s) are waiting. Start bjk-core (it subscribes when it starts).",
            topic,
            waiting,
            typeof(TContext).Name);
    }
}

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Publishes <typeparamref name="TContext"/>'s outbox to a topic.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration.</param>
    /// <param name="topic">The topic, e.g. <c>bjk.users</c>.</param>
    /// <param name="connectionStringName">The module's connection string (the queues' default database).</param>
    public static IServiceCollection AddOutboxPublisher<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string topic,
        string connectionStringName)
        where TContext : DbContext
    {
        services.AddBjkMessaging(configuration, connectionStringName);
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<OutboxPublisher<TContext>>(sp, topic));
        return services;
    }
}
