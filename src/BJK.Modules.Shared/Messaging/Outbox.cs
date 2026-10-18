using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BJK.Modules.Shared.Messaging;

/// <summary>
/// An event waiting to be published. It's saved in the same transaction as the change it describes,
/// then <see cref="OutboxPublisher{TContext}"/> sends it to RabbitMQ, so an event is never lost
/// (or sent for a change that was rolled back). Stored in the module's own schema, table <c>Outbox</c>.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 200;
    public const int LastErrorMaxLength = 1000;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
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
        where TEvent : notnull => new()
    {
        Id = id,
        Type = typeof(TEvent).Name,
        Payload = JsonSerializer.Serialize(payload, JsonOptions),
        OccurredAt = occurredAt,
    };

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
/// Sends a module's outbox to a RabbitMQ fanout exchange, oldest first, waiting for the broker to confirm
/// each message. Runs in every instance of the host; a message may very occasionally be sent twice, so
/// consumers must be idempotent. Published messages are kept for 30 days, then removed.
/// </summary>
public sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes,
    IRabbitMqConnection rabbit,
    string exchange,
    TimeProvider clock,
    ILogger<OutboxPublisher<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepPublished = TimeSpan.FromDays(30);
    private const int BatchSize = 50;

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
                logger.LogError(ex, "Publishing the {Context} outbox to '{Exchange}' failed; retrying shortly.", typeof(TContext).Name, exchange);
                await Task.Delay(ErrorDelay, clock, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
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
            var connection = await rabbit.GetAsync(cancellationToken);
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);

            foreach (var message in pending)
            {
                try
                {
                    var properties = new BasicProperties
                    {
                        Persistent = true,
                        ContentType = "application/json",
                        MessageId = message.Id.ToString("D"),
                        Type = message.Type,
                    };

                    // With publisher confirmations on, this completes once the broker has the message.
                    await channel.BasicPublishAsync(
                        exchange,
                        routingKey: string.Empty,
                        mandatory: false,
                        basicProperties: properties,
                        body: System.Text.Encoding.UTF8.GetBytes(message.Payload),
                        cancellationToken: cancellationToken);
                    message.MarkPublished(clock.GetUtcNow());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    message.MarkFailed(ex.Message);
                    await db.SaveChangesAsync(cancellationToken);
                    throw;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var cutoff = clock.GetUtcNow() - KeepPublished;
        await outbox.Where(m => m.PublishedAt != null && m.PublishedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

        return pending.Count;
    }
}

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Publishes <typeparamref name="TContext"/>'s outbox to a RabbitMQ fanout exchange.</summary>
    public static IServiceCollection AddOutboxPublisher<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string exchange)
        where TContext : DbContext
    {
        services.AddBjkMessaging(configuration);
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<OutboxPublisher<TContext>>(sp, exchange));
        return services;
    }
}
