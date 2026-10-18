using System.Net;
using System.Text;
using BJK.Modules.Communications.Contracts;

namespace BJK.Modules.Communications.Email;

/// <summary>
/// Builds an email in the Bad Jockeys style, black on white, as matching plain-text and HTML bodies. Everything
/// passed in is treated as text and HTML-encoded, so user-supplied values are safe to include.
/// </summary>
/// <example>
/// <code>
/// var message = BrandedEmail.Create("Confirm your email address")
///     .Paragraph("Welcome to Bad Jockeys!")
///     .Button("Confirm email address", link)
///     .Note("If you didn't create an account, you can ignore this email.")
///     .ToMessage(user.Email, "Confirm your Bad Jockeys email address", emailOptions.LogoUrl);
/// </code>
/// </example>
public sealed class BrandedEmail
{
    private const string Ink = "#111111";
    private const string Muted = "#5F5F66";
    private const string Line = "#E3E3DF";
    private const string FontStack = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";

    private readonly string heading;
    private readonly List<Block> blocks = [];

    private BrandedEmail(string heading) => this.heading = heading;

    private enum Kind
    {
        Paragraph,
        Button,
        Code,
        Note,
    }

    public static BrandedEmail Create(string heading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        return new BrandedEmail(heading);
    }

    public BrandedEmail Paragraph(string text) => Add(new Block(Kind.Paragraph, text));

    /// <summary>A call-to-action button. In the plain-text version it becomes "Label: url".</summary>
    /// <exception cref="ArgumentException">The URL isn't an absolute http or https address.</exception>
    public BrandedEmail Button(string label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Button links must be absolute http or https URLs.", nameof(url));
        }

        return Add(new Block(Kind.Button, label, url));
    }

    /// <summary>A code to copy, such as a sign-in code, shown large. In the plain-text version it becomes "Label: code".</summary>
    public BrandedEmail Code(string label, string code) => Add(new Block(Kind.Code, label, code));

    /// <summary>Small grey text, for "if this wasn't you" lines.</summary>
    public BrandedEmail Note(string text) => Add(new Block(Kind.Note, text));

    /// <param name="to">Recipient address.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="logoUrl">Absolute URL of a PNG logo (usually <see cref="EmailOptions.LogoUrl"/>); a text header is used if null.</param>
    public EmailMessage ToMessage(string to, string subject, Uri? logoUrl = null) =>
        new(to, subject, RenderText(), RenderHtml(subject, logoUrl));

    internal string RenderText()
    {
        var text = new StringBuilder();
        text.Append(heading).Append('\n');
        foreach (var block in blocks)
        {
            text.Append('\n');
            text.Append(block.Kind switch
            {
                Kind.Button or Kind.Code => $"{block.Text}: {block.Value}",
                _ => block.Text,
            });
            text.Append('\n');
        }

        text.Append("\n--\nBad Jockeys\n");
        return text.ToString();
    }

    internal string RenderHtml(string subject, Uri? logoUrl)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<meta name=\"color-scheme\" content=\"light\"><title>").Append(E(subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#F4F4F1;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#F4F4F1;\"><tr><td align=\"center\" style=\"padding:32px 16px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#FFFFFF;border:1px solid ").Append(Line).Append(";border-radius:12px;\">")
            .Append("<tr><td style=\"padding:32px 32px 8px 32px;font-family:").Append(FontStack).Append(";color:").Append(Ink).Append(";\">");

        if (logoUrl is { IsAbsoluteUri: true })
        {
            html.Append("<img src=\"").Append(E(logoUrl.AbsoluteUri)).Append("\" width=\"40\" height=\"40\" alt=\"Bad Jockeys\" style=\"display:block;border:0;margin:0 0 24px 0;\">");
        }
        else
        {
            html.Append("<p style=\"margin:0 0 24px 0;font-size:13px;font-weight:700;letter-spacing:0.12em;text-transform:uppercase;\">Bad Jockeys</p>");
        }

        html.Append("<h1 style=\"margin:0 0 16px 0;font-size:22px;line-height:1.3;font-weight:700;\">").Append(E(heading)).Append("</h1>");

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case Kind.Paragraph:
                    html.Append("<p style=\"margin:0 0 16px 0;font-size:16px;line-height:1.5;\">").Append(E(block.Text)).Append("</p>");
                    break;
                case Kind.Button:
                    html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:8px 0 24px 0;\"><tr><td style=\"background:").Append(Ink).Append(";border-radius:8px;\">")
                        .Append("<a href=\"").Append(E(block.Value!)).Append("\" style=\"display:inline-block;padding:12px 22px;font-size:16px;font-weight:600;color:#FFFFFF;text-decoration:none;\">")
                        .Append(E(block.Text)).Append("</a></td></tr></table>")
                        .Append("<p style=\"margin:0 0 16px 0;font-size:13px;line-height:1.5;color:").Append(Muted).Append(";\">Or paste this link into your browser:<br>")
                        .Append("<span style=\"word-break:break-all;\">").Append(E(block.Value!)).Append("</span></p>");
                    break;
                case Kind.Code:
                    html.Append("<p style=\"margin:0 0 6px 0;font-size:13px;color:").Append(Muted).Append(";\">").Append(E(block.Text)).Append("</p>")
                        .Append("<p style=\"margin:0 0 24px 0;padding:14px 16px;background:#F4F4F1;border-radius:8px;font-family:Menlo, Consolas, monospace;font-size:")
                        .Append(block.Value!.Length <= 12 ? "26px;letter-spacing:0.2em;" : "13px;word-break:break-all;")
                        .Append("font-weight:700;\">").Append(E(block.Value)).Append("</p>");
                    break;
                case Kind.Note:
                    html.Append("<p style=\"margin:0 0 16px 0;font-size:13px;line-height:1.5;color:").Append(Muted).Append(";\">").Append(E(block.Text)).Append("</p>");
                    break;
            }
        }

        html.Append("</td></tr>")
            .Append("<tr><td style=\"padding:16px 32px 28px 32px;border-top:1px solid ").Append(Line).Append(";font-family:").Append(FontStack)
            .Append(";font-size:12px;line-height:1.5;color:").Append(Muted).Append(";\">You're getting this email because of activity on your Bad Jockeys account.</td></tr>")
            .Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private BrandedEmail Add(Block block)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(block.Text);
        if (block.Kind is Kind.Button or Kind.Code)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(block.Value);
        }

        blocks.Add(block);
        return this;
    }

    private sealed record Block(Kind Kind, string Text, string? Value = null);
}
