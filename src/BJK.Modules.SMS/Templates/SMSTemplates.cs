using BJK.Modules.SMS.Contracts;

namespace BJK.Modules.SMS.Templates;

/// <summary>
/// Every text message Bad Jockeys sends, kept in git rather than in Twilio. Keep each under 160 characters (plain
/// GSM characters, no emoji) so it fits one SMS segment and costs one message.
/// </summary>
public static class SMSTemplates
{
    /// <summary>Two-factor sign-in by text.</summary>
    public static SMSMessage SignInCode(string to, string code) =>
        new(to, $"Your Bad Jockeys sign-in code is {code}. It expires in a few minutes. Don't share it with anyone.");

    /// <summary>Confirming a mobile number added to an account.</summary>
    public static SMSMessage ConfirmMobileCode(string to, string code) =>
        new(to, $"Your Bad Jockeys code to confirm this number is {code}. If you didn't ask for it, ignore this text.");
}
