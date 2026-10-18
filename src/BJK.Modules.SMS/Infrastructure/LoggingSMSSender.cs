using BJK.Modules.SMS.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.SMS.Infrastructure;

/// <summary>
/// <c>SMS:Provider</c> = <c>Log</c>: nothing is sent. In Development the message is written to the log; elsewhere
/// only the fact that it was dropped is logged.
/// </summary>
internal sealed class LoggingSMSSender(IHostEnvironment environment, ILogger<LoggingSMSSender> logger) : ISMSSender
{
    public Task SendAsync(SMSMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!PhoneNumbers.IsE164(message.To))
        {
            throw new ArgumentException("The phone number must be in E.164 format, e.g. +447700900123.", nameof(message));
        }

        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "DEVELOPMENT ONLY - SMS not sent (SMS:Provider is Log).\nTo: {To}\n{Body}",
                message.To,
                message.Body);
        }
        else
        {
            logger.LogError("An SMS was not sent because no SMS provider is configured (SMS:Provider is Log).");
        }

        return Task.CompletedTask;
    }
}
