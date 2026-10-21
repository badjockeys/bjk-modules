using BJK.Modules.SMS.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.SMS.Infrastructure;

/// <summary>
/// No Twilio settings: nothing is sent. In Development the message is written to the log; elsewhere
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
                "DEVELOPMENT ONLY - SMS not sent (no SMS:Twilio settings).\nTo: {To}\n{Body}",
                message.To,
                message.Body);
        }
        else
        {
            logger.LogError("An SMS was not sent because SMS:Twilio isn't set up.");
        }

        return Task.CompletedTask;
    }
}
