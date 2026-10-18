using BJK.Modules.Media.Infrastructure.Processing;
using RabbitMQ.Client;
using Xunit;

namespace BJK.Modules.Media.Tests.Processing;

public sealed class MediaProcessingWorkerTests
{
    [Fact]
    public void The_first_delivery_has_no_count()
    {
        Assert.Equal(0, MediaProcessingWorker.DeliveryCount(new BasicProperties()));
    }

    [Fact]
    public void Redeliveries_read_the_quorum_queue_count()
    {
        var properties = new BasicProperties { Headers = new Dictionary<string, object?> { ["x-delivery-count"] = 2L } };

        Assert.Equal(2, MediaProcessingWorker.DeliveryCount(properties));
    }
}
