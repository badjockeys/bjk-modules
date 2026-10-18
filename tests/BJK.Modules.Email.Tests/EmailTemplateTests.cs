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

    [Fact]
    public void Report_acknowledgements_give_the_reference_and_target_time()
    {
        var urgent = EmailTemplates.ReportReceived("R-1A2B3C4D", "a post", urgent: true, TimeSpan.FromHours(4)).To("sam@example.com");
        var normal = EmailTemplates.ReportReceived("R-1A2B3C4D", "a comment", urgent: false, TimeSpan.FromHours(48)).To("sam@example.com");

        Assert.Equal("We've received your report (R-1A2B3C4D)", urgent.Subject);
        Assert.Contains("Your reference: R-1A2B3C4D\n", urgent.TextBody);
        Assert.Contains("within 4 hours", urgent.TextBody);
        Assert.Contains("999", urgent.TextBody);
        Assert.Contains("within 48 hours", normal.TextBody);
        Assert.Contains("a comment", normal.TextBody);
        Assert.Contains("because you reported content", normal.HtmlBody);
    }

    [Fact]
    public void Report_outcomes_say_what_happened_and_how_to_disagree()
    {
        var removed = EmailTemplates.ReportResolved("R-1", contentRemoved: true).To("sam@example.com");
        var kept = EmailTemplates.ReportResolved("R-1", contentRemoved: false).To("sam@example.com");

        Assert.Contains("has removed it", removed.TextBody);
        Assert.Contains("left up", kept.TextBody);
        Assert.Contains("reply to this email", kept.TextBody);
    }

    [Fact]
    public void Appeal_emails_confirm_and_decide()
    {
        var received = EmailTemplates.AppealReceived("A-9", TimeSpan.FromHours(48)).To("owner@example.com");
        var overturned = EmailTemplates.AppealDecided("A-9", overturned: true).To("owner@example.com");
        var upheld = EmailTemplates.AppealDecided("A-9", overturned: false).To("owner@example.com");

        Assert.Contains("within 48 hours", received.TextBody);
        Assert.Contains("visible again", overturned.TextBody);
        Assert.Contains("stands", upheld.TextBody);
    }
}
