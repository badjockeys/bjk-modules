using BJK.Modules.Email.Templates;
using Xunit;

namespace BJK.Modules.Email.Tests;

public sealed class EmailTemplateTests
{
    [Fact]
    public void Confirm_email_links_to_the_confirmation_page()
    {
        var message = EmailTemplates.ConfirmEmail("https://auth.badjockeys.com/Account/ConfirmEmail?userId=1&code=abc")
            .To("rider@example.com");

        Assert.Equal("Confirm your Bad Jockeys email address", message.Subject);
        Assert.Contains("Confirm email address: https://auth.badjockeys.com/Account/ConfirmEmail?userId=1&code=abc\n", message.TextBody);
    }

    [Fact]
    public void Password_reset_always_has_the_code_and_a_link_when_there_is_a_page()
    {
        var withLink = EmailTemplates.PasswordReset("TOKEN123", "https://badjockeys.com/reset?token=TOKEN123").To("a@example.com");
        var codeOnly = EmailTemplates.PasswordReset("TOKEN123", null).To("a@example.com");

        Assert.Contains("Password reset code: TOKEN123\n", withLink.TextBody);
        Assert.Contains("Reset your password: https://badjockeys.com/reset?token=TOKEN123\n", withLink.TextBody);
        Assert.Contains("Password reset code: TOKEN123\n", codeOnly.TextBody);
        Assert.DoesNotContain("https://", codeOnly.TextBody);
    }

    [Fact]
    public void Sign_in_code_email_shows_the_code()
    {
        var message = EmailTemplates.SignInCode("123456").To("a@example.com", new Uri("https://badjockeys.com/logo.png"));

        Assert.Equal("Your Bad Jockeys sign-in code", message.Subject);
        Assert.Contains("Sign-in code: 123456\n", message.TextBody);
        Assert.Contains("https://badjockeys.com/logo.png", message.HtmlBody);
    }
}
