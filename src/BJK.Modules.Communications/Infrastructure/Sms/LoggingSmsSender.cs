using BJK.Modules.Communications.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Communications.Infrastructure.Sms;

/// <summary>
/// <c>Sms:Provider</c> = <c>Log</c>: nothing is sent. In Development the message is written to the log; elsewhere
/// only the fact that it was dropped is logged.
/// </summary>
internal sealed class LoggingSmsSender(IHostEnvironment environment, ILogger<LoggingSmsSender> logger) : ISmsSender
{
    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!PhoneNumbers.IsE164(message.To))
        {
            throw new ArgumentException("The phone number must be in E.164 format, e.g. +447700900123.", nameof(message));
        }

        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "DEVELOPMENT ONLY - SMS not sent (Sms:Provider is Log).\nTo: {To}\n{Body}",
                message.To,
                message.Body);
        }
        else
        {
            logger.LogError("An SMS was not sent because no SMS provider is configured (Sms:Provider is Log).");
        }

        return Task.CompletedTask;
    }
}
