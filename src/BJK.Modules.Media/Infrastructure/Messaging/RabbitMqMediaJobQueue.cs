using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>Publishes processing jobs to RabbitMQ. One long-lived connection; a channel per publish.</summary>
internal sealed class RabbitMqMediaJobQueue(IOptions<MediaQueueOptions> options) : IMediaJobQueue, IAsyncDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MediaQueueOptions _options = options.Value;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IConnection? _connection;
    private bool _topologyDeclared;

    public async Task EnqueueAsync(MediaProcessingJob job, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        if (!_topologyDeclared)
        {
            await MediaQueueTopology.DeclareAsync(channel, _options.QueueName, cancellationToken);
            _topologyDeclared = true;
        }

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = job.MediaId.ToString("D"),
            Type = nameof(MediaProcessingJob),
        };

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: _options.QueueName,
            mandatory: false,
            basicProperties: properties,
            body: JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions),
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connectLock.Dispose();
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = _options.ConnectionUri!,
                ClientProvidedName = "bjk-media-api",
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
