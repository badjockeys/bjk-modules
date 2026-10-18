using BJK.Modules.Communications.Contracts;

namespace BJK.Modules.Communications.Templates;

/// <summary>
/// Every text message Bad Jockeys sends, kept in git rather than in Twilio. Keep each under 160 characters (plain
/// GSM characters, no emoji) so it fits one SMS segment and costs one message.
/// </summary>
public static class SmsTemplates
{
    /// <summary>Two-factor sign-in by text.</summary>
    public static SmsMessage SignInCode(string to, string code) =>
        new(to, $"Your Bad Jockeys sign-in code is {code}. It expires in a few minutes. Don't share it with anyone.");
}
