using Microsoft.Extensions.Options;

namespace BJK.Modules.Communications;

/// <summary>Checks the email settings at startup so a missing key fails the deploy, not the first sign-up.</summary>
internal sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();
        switch (options.Provider)
        {
            case EmailOptions.LogProvider:
                break;
            case EmailOptions.SendGridProvider:
                if (string.IsNullOrWhiteSpace(options.FromAddress))
                {
                    failures.Add("Email:FromAddress is required when Email:Provider is SendGrid.");
                }

                if (string.IsNullOrWhiteSpace(options.SendGrid.ApiKey))
                {
                    failures.Add("Email:SendGrid:ApiKey is required when Email:Provider is SendGrid.");
                }

                break;
            default:
                failures.Add($"Email:Provider must be '{EmailOptions.LogProvider}' or '{EmailOptions.SendGridProvider}'.");
                break;
        }

        if (options.LogoUrl is { IsAbsoluteUri: false })
        {
            failures.Add("Email:LogoUrl must be an absolute URL.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Checks the SMS settings at startup.</summary>
internal sealed class SmsOptionsValidator : IValidateOptions<SmsOptions>
{
    public ValidateOptionsResult Validate(string? name, SmsOptions options)
    {
        var failures = new List<string>();
        switch (options.Provider)
        {
            case SmsOptions.LogProvider:
                break;
            case SmsOptions.TwilioProvider:
                var twilio = options.Twilio;
                if (string.IsNullOrWhiteSpace(twilio.AccountSid))
                {
                    failures.Add("Sms:Twilio:AccountSid is required when Sms:Provider is Twilio.");
                }

                var hasApiKey = !string.IsNullOrWhiteSpace(twilio.ApiKeySid);
                if (hasApiKey && string.IsNullOrWhiteSpace(twilio.ApiKeySecret))
                {
                    failures.Add("Sms:Twilio:ApiKeySecret is required with Sms:Twilio:ApiKeySid.");
                }

                if (!hasApiKey && string.IsNullOrWhiteSpace(twilio.AuthToken))
                {
                    failures.Add("Set Sms:Twilio:ApiKeySid and ApiKeySecret (recommended) or Sms:Twilio:AuthToken.");
                }

                if (string.IsNullOrWhiteSpace(twilio.MessagingServiceSid) && string.IsNullOrWhiteSpace(twilio.FromNumber))
                {
                    failures.Add("Set Sms:Twilio:MessagingServiceSid (recommended) or Sms:Twilio:FromNumber.");
                }

                break;
            default:
                failures.Add($"Sms:Provider must be '{SmsOptions.LogProvider}' or '{SmsOptions.TwilioProvider}'.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
