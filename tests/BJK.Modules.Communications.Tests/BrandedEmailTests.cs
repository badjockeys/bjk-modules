using BJK.Modules.Communications.Email;

namespace BJK.Modules.Communications.Tests;

public sealed class BrandedEmailTests
{
    [Fact]
    public void Builds_matching_text_and_html_bodies()
    {
        var message = BrandedEmail.Create("Confirm your email address")
            .Paragraph("Welcome to Bad Jockeys!")
            .Button("Confirm email address", "https://auth.badjockeys.com/Account/ConfirmEmail?userId=1&code=abc")
            .Code("Sign-in code", "123456")
            .Note("If you didn't create an account, you can ignore this email.")
            .ToMessage("rider@example.com", "Confirm your Bad Jockeys email address");

        Assert.Equal("rider@example.com", message.To);
        Assert.Equal("Confirm your Bad Jockeys email address", message.Subject);
        Assert.Equal(
            "Confirm your email address\n\nWelcome to Bad Jockeys!\n\n" +
            "Confirm email address: https://auth.badjockeys.com/Account/ConfirmEmail?userId=1&code=abc\n\n" +
            "Sign-in code: 123456\n\n" +
            "If you didn't create an account, you can ignore this email.\n\n--\nBad Jockeys\n",
            message.TextBody);

        Assert.NotNull(message.HtmlBody);
        Assert.Contains("<h1", message.HtmlBody);
        Assert.Contains("href=\"https://auth.badjockeys.com/Account/ConfirmEmail?userId=1&amp;code=abc\"", message.HtmlBody);
        Assert.Contains(">123456</p>", message.HtmlBody);
        Assert.Contains("If you didn&#39;t create an account", message.HtmlBody);
    }

    [Fact]
    public void Encodes_everything_so_user_text_cannot_inject_html()
    {
        var message = BrandedEmail.Create("Hi <b>there</b>")
            .Paragraph("<script>alert(1)</script>")
            .ToMessage("rider@example.com", "<img src=x>");

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.DoesNotContain("<b>", message.HtmlBody);
        Assert.DoesNotContain("<img src=x>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/file")]
    public void Only_allows_http_and_https_links(string url)
    {
        Assert.Throws<ArgumentException>(() => BrandedEmail.Create("Heading").Button("Click", url));
    }

    [Fact]
    public void Shows_the_logo_when_given_otherwise_a_text_header()
    {
        var withLogo = BrandedEmail.Create("Heading").Paragraph("Body")
            .ToMessage("a@example.com", "Subject", new Uri("https://badjockeys.com/email-logo.png"));
        var withoutLogo = BrandedEmail.Create("Heading").Paragraph("Body").ToMessage("a@example.com", "Subject");

        Assert.Contains("<img src=\"https://badjockeys.com/email-logo.png\"", withLogo.HtmlBody);
        Assert.DoesNotContain("<img", withoutLogo.HtmlBody);
        Assert.Contains(">Bad Jockeys</p>", withoutLogo.HtmlBody);
    }

    [Fact]
    public void Long_codes_wrap_instead_of_stretching_the_email()
    {
        var token = new string('A', 200);
        var message = BrandedEmail.Create("Reset").Code("Password reset code", token).ToMessage("a@example.com", "Reset");

        Assert.Contains("word-break:break-all", message.HtmlBody);
        Assert.Contains($"Password reset code: {token}\n", message.TextBody);
    }
}
