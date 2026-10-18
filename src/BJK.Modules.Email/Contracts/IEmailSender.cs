using BJK.Modules.Shared.Delivery;

namespace BJK.Modules.Email.Contracts;

/// <summary>An email to one person. Build them from <see cref="Templates.EmailTemplates"/>.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">Plain-text version. Always sent, for clients that don't show HTML.</param>
/// <param name="HtmlBody">Optional HTML version.</param>
public sealed record EmailMessage(string To, string Subject, string TextBody, string? HtmlBody = null);

/// <summary>
/// Sends email. The provider is chosen by <c>Email:Provider</c>: <c>SendGrid</c>, or <c>Log</c> for local development.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends the email, retrying brief provider outages.</summary>
    /// <exception cref="MessageDeliveryException">The provider refused the email or stayed unavailable.</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
