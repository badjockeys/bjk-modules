using Microsoft.Extensions.Options;

namespace BJK.Modules.Email;

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
