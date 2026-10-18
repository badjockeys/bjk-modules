using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace BJK.Modules.Shared.Messaging;

/// <summary>The host's one long-lived RabbitMQ connection, opened on first use and reopened if it drops.</summary>
public interface IRabbitMqConnection
{
    Task<IConnection> GetAsync(CancellationToken cancellationToken);
}

internal sealed class RabbitMqConnection(IOptions<MessagingOptions> options, IHostEnvironment environment)
    : IRabbitMqConnection, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                Uri = options.Value.ConnectionUri!,
                ClientProvidedName = environment.ApplicationName,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers the shared RabbitMQ connection and its settings. Safe to call from several modules.</summary>
    public static IServiceCollection AddBjkMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IRabbitMqConnection)))
        {
            return services;
        }

        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        return services;
    }
}
