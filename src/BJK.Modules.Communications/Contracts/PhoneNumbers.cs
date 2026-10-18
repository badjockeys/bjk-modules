using System.Text.RegularExpressions;

namespace BJK.Modules.Communications.Contracts;

/// <summary>Phone number checks shared by the SMS providers and callers that collect numbers.</summary>
public static partial class PhoneNumbers
{
    /// <summary>True for an E.164 number: a plus, a country code and up to 15 digits in all, e.g. +447700900123.</summary>
    public static bool IsE164(string? number) => number is not null && E164().IsMatch(number);

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164();
}
