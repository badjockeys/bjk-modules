using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Layout;

namespace BJK.Modules.Email.Templates;

/// <summary>A subject and body, ready to address to someone.</summary>
public sealed record EmailTemplate(string Subject, BrandedEmail Body)
{
    /// <param name="address">Recipient address.</param>
    /// <param name="logoUrl">Usually <see cref="EmailOptions.LogoUrl"/>.</param>
    public EmailMessage To(string address, Uri? logoUrl = null) => Body.ToMessage(address, Subject, logoUrl);
}

/// <summary>
/// Every email Bad Jockeys sends. The wording lives here, in git, rather than in SendGrid, so changes are reviewed,
/// versioned and tested like code. Add new emails here, not in the hosts.
/// </summary>
public static class EmailTemplates
{
    /// <summary>After registration, or when the user asks for another link.</summary>
    public static EmailTemplate ConfirmEmail(string confirmUrl) => new(
        "Confirm your Bad Jockeys email address",
        BrandedEmail.Create("Confirm your email address")
            .Paragraph("Welcome to Bad Jockeys!")
            .Button("Confirm email address", confirmUrl)
            .Paragraph("You need to confirm it before you can post or comment.")
            .Note("If you didn't create an account, you can ignore this email."));

    /// <summary>Forgot password. The code always appears; the button only when there's a reset page to link to.</summary>
    public static EmailTemplate PasswordReset(string resetCode, string? resetUrl)
    {
        var body = BrandedEmail.Create("Reset your password")
            .Paragraph("We received a request to reset the password for your Bad Jockeys account.");
        if (resetUrl is not null)
        {
            body.Button("Reset your password", resetUrl);
        }

        body.Code("Password reset code", resetCode)
            .Note("If you didn't ask for this, you can ignore this email. Your password won't change.");
        return new EmailTemplate("Reset your Bad Jockeys password", body);
    }

    /// <summary>Two-factor sign-in by email.</summary>
    public static EmailTemplate SignInCode(string code) => new(
        "Your Bad Jockeys sign-in code",
        BrandedEmail.Create("Your sign-in code")
            .Paragraph("Enter this code to finish signing in to Bad Jockeys. It expires in a few minutes.")
            .Code("Sign-in code", code)
            .Note("If you didn't try to sign in, change your password."));
}
