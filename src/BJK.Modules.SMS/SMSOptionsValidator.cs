using Microsoft.Extensions.Options;

namespace BJK.Modules.SMS;

/// <summary>Checks the SMS settings at startup.</summary>
internal sealed class SMSOptionsValidator : IValidateOptions<SMSOptions>
{
    public ValidateOptionsResult Validate(string? name, SMSOptions options)
    {
        var failures = new List<string>();
        if (options.UsesTwilio)
        {
            var twilio = options.Twilio;
            var hasApiKey = !string.IsNullOrWhiteSpace(twilio.ApiKeySid);
            if (hasApiKey && string.IsNullOrWhiteSpace(twilio.ApiKeySecret))
            {
                failures.Add("SMS:Twilio:ApiKeySecret is required with SMS:Twilio:ApiKeySid.");
            }

            if (!hasApiKey && string.IsNullOrWhiteSpace(twilio.AuthToken))
            {
                failures.Add("Set SMS:Twilio:ApiKeySid and ApiKeySecret (recommended) or SMS:Twilio:AuthToken.");
            }

            if (string.IsNullOrWhiteSpace(twilio.MessagingServiceSid) && string.IsNullOrWhiteSpace(twilio.FromNumber))
            {
                failures.Add("Set SMS:Twilio:MessagingServiceSid (recommended) or SMS:Twilio:FromNumber.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
