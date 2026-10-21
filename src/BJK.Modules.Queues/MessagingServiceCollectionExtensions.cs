using BJK.Modules.Queues.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Queues;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers durable messaging (<see cref="IMessageSender"/>) over SQL Server queues and its settings
    /// (<see cref="MessagingOptions"/>). Safe to call from several modules: the first call decides the default database.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration (<c>Queues</c>, <c>ConnectionStrings</c>).</param>
    /// <param name="connectionStringName">
    /// The module's own connection string, used for the queues unless the host chose another with <see cref="UseQueuesIn"/>.
    /// </param>
    public static IServiceCollection AddBjkMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);
        if (services.Any(d => d.ServiceType == typeof(MessagingDatabase)))
        {
            return services;
        }

        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.MaxRetryDelay >= o.RetryDelay, "Queues:MaxRetryDelay must be at least Queues:RetryDelay.")
            .Validate(
                o => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringNameFor(o, connectionStringName))),
                $"The queues' connection string isn't configured (ConnectionStrings:{connectionStringName}, or the one the host chose with UseQueuesIn).")
            .ValidateOnStart();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
            var name = ConnectionStringNameFor(options, connectionStringName);
            var connectionString = configuration.GetConnectionString(name)
                ?? throw new InvalidOperationException($"Connection string '{name}' (for messaging) is not configured.");
            return new MessagingDatabase(connectionString, options.Schema);
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMessageSender, SqlServerMessageSender>();
        return services;
    }

    /// <summary>
    /// Reads a queue: each message goes to <typeparamref name="TConsumer"/> (resolved in a new scope per message), with
    /// retries after a growing delay, and the dead-letter table <c>{queue}.failed</c> after
    /// <see cref="MessageConsumerDefinition.MaxAttempts"/>. Several replicas of the host share the work (competing
    /// consumers). Registering the same queue twice keeps the first registration.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Its configuration.</param>
    /// <param name="connectionStringName">The module's own connection string (see <see cref="AddBjkMessaging"/>).</param>
    /// <param name="definition">The queue, attempts, concurrency and topics.</param>
    public static IServiceCollection AddMessageConsumer<TConsumer>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName,
        MessageConsumerDefinition definition)
        where TConsumer : class, IMessageConsumer
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Queue);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Concurrency, 1);

        services.AddBjkMessaging(configuration, connectionStringName);

        var alreadyRead = services.Any(d =>
            d.ServiceType == typeof(MessageConsumerRegistration)
            && d.ImplementationInstance is MessageConsumerRegistration existing
            && string.Equals(existing.Definition.Queue, definition.Queue, StringComparison.OrdinalIgnoreCase));
        if (alreadyRead)
        {
            return services;
        }

        services.TryAddScoped<TConsumer>();
        services.AddSingleton(new MessageConsumerRegistration(definition, typeof(TConsumer)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MessageConsumerHost>());
        return services;
    }

    /// <summary>
    /// Puts this host's queues in another database than its modules' data, e.g. the identity server writing user events
    /// to bjk-core's queues in <c>BadJockeysDatabase</c>. With <paramref name="createTables"/> false the host never
    /// creates queue tables (its login may only use the ones bjk-core created). Call it once, from the host.
    /// </summary>
    public static IServiceCollection UseQueuesIn(this IServiceCollection services, string connectionStringName, bool createTables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);
        services.PostConfigure<MessagingOptions>(o =>
        {
            o.ConnectionStringName = connectionStringName;
            o.CreateTables = createTables;
        });
        return services;
    }

    private static string ConnectionStringNameFor(MessagingOptions options, string defaultName) =>
        string.IsNullOrWhiteSpace(options.ConnectionStringName) ? defaultName : options.ConnectionStringName;
}
