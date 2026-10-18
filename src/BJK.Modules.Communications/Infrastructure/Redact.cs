namespace BJK.Modules.Communications.Infrastructure;

/// <summary>Masks addresses for logs outside Development: enough to trace a delivery, not enough to identify someone.</summary>
internal static class Redact
{
    /// <summary>rider.one@example.com → r***@example.com</summary>
    public static string Email(string address)
    {
        var at = address.LastIndexOf('@');
        return at <= 0 ? "***" : string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(at));
    }

    /// <summary>+447700900123 → +44********23</summary>
    public static string Phone(string number) =>
        number.Length <= 5 ? "***" : string.Concat(number.AsSpan(0, 3), new string('*', number.Length - 5), number.AsSpan(number.Length - 2));
}
