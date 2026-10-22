using System.ComponentModel.DataAnnotations;

namespace BJK.Modules.Queues;

/// <summary>
/// Durable queues and events, stored in SQL Server (section <c>Queues</c>). Every value has a default. Which database
/// holds the queues isn't a setting: it's the database of the module that registers messaging
/// (<c>BadJockeysDatabase</c> in bjk-core), unless the host says otherwise in code with
/// <see cref="MessagingServiceCollectionExtensions.UseQueuesIn"/> (the identity server, which writes to bjk-core's queues).
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Queues";

    /// <summary>
    /// The connection string (a name under <c>ConnectionStrings</c>) for the database holding the queue tables. Not
    /// read from configuration: null means the registering module's database; a host changes it with
    /// <see cref="MessagingServiceCollectionExtensions.UseQueuesIn"/>.
    /// </summary>
    public string? ConnectionStringName { get; internal set; }

    /// <summary>The schema holding the queue tables (one table per queue, plus <c>subscriptions</c>).</summary>
    [Required]
    [RegularExpression("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    public string Schema { get; set; } = "messaging";

    /// <summary>How long an idle consumer waits before looking for new messages again.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a consumer holds a message while working on it. The lease is renewed while the work goes on, so this is
    /// how soon a message is retried after a worker crashes or is stopped without finishing.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The wait before the first retry of a failed message; each further retry waits twice as long, up to
    /// <see cref="MaxRetryDelay"/>.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between two attempts.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Create missing queue tables (and the schema) on startup. Not read from configuration: on, unless the host turns
    /// it off with <see cref="MessagingServiceCollectionExtensions.UseQueuesIn"/> (the identity server, whose login may
    /// only read and write the queues bjk-core created).
    /// </summary>
    public bool CreateTables { get; internal set; } = true;

    /// <summary>
    /// The queue this host reads user lifecycle events from (bjk-core). It subscribes to the
    /// <see cref="BJK.Modules.Shared.Users.UserLifecycleEvents.Topic"/> topic, so each event published there is copied into it.
    /// </summary>
    [Required]
    public string UserEvents { get; set; } = "bjk-core.user-events";

    /// <summary>
    /// The queue this host reads media events (<c>media.ready</c>, <c>media.failed</c>) from, subscribed to the Media
    /// module's <c>bjk.media</c> topic. The Posts module uses them to publish posts once their media are ready.
    /// </summary>
    [Required]
    public string MediaEvents { get; set; } = "bjk-core.media-events";

    /// <summary>The queue of media processing jobs, read by the media worker.</summary>
    [Required]
    public string MediaProcessing { get; set; } = "media.process";
}
