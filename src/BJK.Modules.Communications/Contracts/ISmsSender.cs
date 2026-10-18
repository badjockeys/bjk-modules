namespace BJK.Modules.Communications.Contracts;

/// <summary>A text message to one phone number.</summary>
/// <param name="To">Recipient in E.164 format, e.g. +447700900123.</param>
/// <param name="Body">Message text. Keep it under 160 characters to fit one SMS segment.</param>
public sealed record SmsMessage(string To, string Body);

/// <summary>
/// Sends SMS. The provider is chosen by <c>Sms:Provider</c>: <c>Twilio</c>, or <c>Log</c> for local development.
/// </summary>
public interface ISmsSender
{
    /// <summary>Sends the message, retrying brief provider outages.</summary>
    /// <exception cref="ArgumentException">The number isn't in E.164 format.</exception>
    /// <exception cref="MessageDeliveryException">The provider refused the message or stayed unavailable.</exception>
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken);
}
