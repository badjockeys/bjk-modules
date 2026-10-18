using System.Text.Json;
using System.Text.Json.Serialization;
using BJK.Modules.Shared.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace BJK.Modules.Media.Infrastructure.Messaging;

/// <summary>Publishes processing jobs to RabbitMQ over the host's shared connection; a channel per publish.</summary>
internal sealed class RabbitMqMediaJobQueue(IRabbitMqConnection rabbit, IOptions<MediaQueueOptions> options) : IMediaJobQueue
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MediaQueueOptions _options = options.Value;
    private bool _topologyDeclared;

    public async Task EnqueueAsync(MediaProcessingJob job, CancellationToken cancellationToken)
    {
        var connection = await rabbit.GetAsync(cancellationToken);
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
}
