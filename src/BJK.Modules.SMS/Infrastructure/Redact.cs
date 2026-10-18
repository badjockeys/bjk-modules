namespace BJK.Modules.SMS.Infrastructure;

/// <summary>Masks phone numbers for logs outside Development: enough to trace a delivery, not enough to identify someone.</summary>
internal static class Redact
{
    /// <summary>+447700900123 → +44********23</summary>
    public static string Phone(string number) =>
        number.Length <= 5 ? "***" : string.Concat(number.AsSpan(0, 3), new string('*', number.Length - 5), number.AsSpan(number.Length - 2));
}
