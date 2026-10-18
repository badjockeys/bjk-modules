using System.ComponentModel.DataAnnotations;

namespace BJK.Modules.Shared.Messaging;

/// <summary>RabbitMQ settings shared by every module in a host. Section <c>Messaging</c>.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>e.g. amqp://user:password@host:5672/vhost. Secret: user secrets locally, Key Vault in Azure.</summary>
    [Required]
    public Uri? ConnectionUri { get; set; }

    /// <summary>
    /// The queue this host reads user lifecycle events from. Each host that reacts to them (bjk-core)
    /// has its own queue bound to the <see cref="Users.UserLifecycleEvents.Exchange"/> exchange.
    /// </summary>
    [Required]
    public string UserEventsQueue { get; set; } = "bjk-core.user-events";
}
