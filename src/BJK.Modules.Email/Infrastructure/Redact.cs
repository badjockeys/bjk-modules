namespace BJK.Modules.Email.Infrastructure;

/// <summary>Masks addresses for logs outside Development: enough to trace a delivery, not enough to identify someone.</summary>
internal static class Redact
{
    /// <summary>rider.one@example.com → r***@example.com</summary>
    public static string Email(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 0 ? "***" : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(at));
    }
}
