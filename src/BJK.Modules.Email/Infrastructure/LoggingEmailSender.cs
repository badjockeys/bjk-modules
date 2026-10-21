using BJK.Modules.Email.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Email.Infrastructure;

/// <summary>
/// No SendGrid key: nothing leaves the server. In Development the whole plain-text email, including
/// any code or link, is written to the log so flows can be tested end to end. In any other environment only the fact
/// that an email was dropped is logged, never its content.
/// </summary>
internal sealed class LoggingEmailSender(IHostEnvironment environment, ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (environment.IsDevelopment())
        {
            logger.LogWarning(
                "DEVELOPMENT ONLY - email not sent (no Email:SendGrid:ApiKey).\nTo: {To}\nSubject: {Subject}\n{Body}",
                message.To,
                message.Subject,
                message.TextBody);
        }
        else
        {
            logger.LogError(
                "Email \"{Subject}\" was not sent because Email:SendGrid:ApiKey isn't set.",
                message.Subject);
        }

        return Task.CompletedTask;
    }
}
