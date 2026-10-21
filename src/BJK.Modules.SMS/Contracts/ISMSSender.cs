using BJK.Modules.Shared.Delivery;

namespace BJK.Modules.SMS.Contracts;

/// <summary>A text message to one phone number.</summary>
/// <param name="To">Recipient in E.164 format, e.g. +447700900123.</param>
/// <param name="Body">Message text. Keep it under 160 characters to fit one SMS segment.</param>
public sealed record SMSMessage(string To, string Body);

/// <summary>
/// Sends SMS. Twilio when <c>SMS:Twilio:AccountSid</c> is set; otherwise only logged (local development).
/// </summary>
public interface ISMSSender
{
    /// <summary>Sends the message, retrying brief provider outages.</summary>
    /// <exception cref="ArgumentException">The number isn't in E.164 format.</exception>
    /// <exception cref="MessageDeliveryException">The provider refused the message or stayed unavailable.</exception>
    Task SendAsync(SMSMessage message, CancellationToken cancellationToken);
}
