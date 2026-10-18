namespace BJK.Modules.Shared.Delivery;

/// <summary>An email or SMS couldn't be handed to the provider (SendGrid, Twilio).</summary>
public sealed class MessageDeliveryException : Exception
{
    public MessageDeliveryException(string message, bool isTransient, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    /// <summary>
    /// True when the provider was unavailable (trying again later may work); false when it rejected the
    /// message, e.g. an invalid number or a bad API key.
    /// </summary>
    public bool IsTransient { get; }
}
