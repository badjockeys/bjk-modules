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

    /// <summary>
    /// Sent straight after someone reports content, whether through the app or the public "Report content" form.
    /// </summary>
    /// <param name="reference">The report's reference, e.g. R-1A2B3C4D.</param>
    /// <param name="what">What was reported, e.g. "a post" or "a comment".</param>
    /// <param name="urgent">True for illegal content and threats, which are looked at first.</param>
    /// <param name="respondWithin">The target time for a decision.</param>
    public static EmailTemplate ReportReceived(string reference, string what, bool urgent, TimeSpan respondWithin)
    {
        var body = BrandedEmail.Create("We've received your report")
            .Paragraph($"Thank you for reporting {what} on Bad Jockeys. A moderator will review it.")
            .Code("Your reference", reference)
            .Paragraph(urgent
                ? $"Because it may involve illegal content or a threat to someone's safety, we treat it as urgent and aim to act within {Hours(respondWithin)}."
                : $"We aim to review it within {Hours(respondWithin)}, and we'll email you when we've decided.")
            .Note("If someone is in immediate danger, call 999. Please quote your reference if you contact us about this report.")
            .Footer(ReportFooter);
        return new EmailTemplate($"We've received your report ({reference})", body);
    }

    /// <summary>Sent when a moderator has decided what to do about a report.</summary>
    /// <param name="reference">The report's reference.</param>
    /// <param name="contentRemoved">True if the content was taken down or changed; false if it was left up.</param>
    public static EmailTemplate ReportResolved(string reference, bool contentRemoved)
    {
        var body = BrandedEmail.Create("We've reviewed your report")
            .Code("Your reference", reference)
            .Paragraph(contentRemoved
                ? "Thank you. A moderator reviewed the content you reported and has removed it from Bad Jockeys."
                : "Thank you. A moderator reviewed the content you reported and decided it doesn't break our rules, so it has been left up.")
            .Paragraph("If you don't agree with this decision, reply to this email quoting your reference and a different moderator will take another look.")
            .Footer(ReportFooter);
        return new EmailTemplate($"Your report has been reviewed ({reference})", body);
    }

    /// <summary>Sent when someone appeals a moderation decision about their own post or comment.</summary>
    /// <param name="reference">The appeal's reference, e.g. A-1A2B3C4D.</param>
    /// <param name="respondWithin">The target time for a decision.</param>
    public static EmailTemplate AppealReceived(string reference, TimeSpan respondWithin) => new(
        $"We've received your appeal ({reference})",
        BrandedEmail.Create("We've received your appeal")
            .Paragraph("Thank you. A moderator who wasn't involved in the original decision will review your appeal.")
            .Code("Your reference", reference)
            .Paragraph($"We aim to decide within {Hours(respondWithin)}, and we'll email you when we have.")
            .Note("Each decision can be appealed once."));

    /// <summary>Sent when a moderator has decided an appeal.</summary>
    /// <param name="reference">The appeal's reference.</param>
    /// <param name="overturned">True if the content has been restored; false if the original decision stands.</param>
    public static EmailTemplate AppealDecided(string reference, bool overturned) => new(
        $"Your appeal has been decided ({reference})",
        BrandedEmail.Create("Your appeal has been decided")
            .Code("Your reference", reference)
            .Paragraph(overturned
                ? "We've looked again and reversed the decision. Your content is visible again."
                : "We've looked again and the original decision stands. Your content will stay hidden.")
            .Note("This was your one appeal for this decision. Our rules are in the Bad Jockeys terms of use."));

    /// <summary>
    /// Sent when a post made while its photos or video were still processing can't be published, because one of them
    /// failed processing, was rejected by a moderator or was deleted.
    /// </summary>
    /// <param name="postTitle">The post's title, so the author knows which one.</param>
    /// <param name="reason">Why, as shown on the post (e.g. "Videos can be up to 30 seconds long...").</param>
    public static EmailTemplate PostNotPublished(string postTitle, string reason) => new(
        "Your Bad Jockeys post couldn't be published",
        BrandedEmail.Create("Your post couldn't be published")
            .Paragraph($"We couldn't publish your post \u201c{postTitle}\u201d because one of its photos or videos couldn't be used:")
            .Paragraph(reason)
            .Paragraph("Nobody else can see the post. You can delete it from My posts, then upload the photos or video again and post it once more.")
            .Note("You're getting this email because you posted on Bad Jockeys before your photos or video had finished processing."));

    private const string ReportFooter = "You're getting this email because you reported content on Bad Jockeys.";

    private static string Hours(TimeSpan duration)
    {
        var hours = (int)Math.Ceiling(duration.TotalHours);
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
