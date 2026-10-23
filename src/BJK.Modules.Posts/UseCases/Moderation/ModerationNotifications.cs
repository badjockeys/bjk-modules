using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Shared.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Moderation;

/// <summary>
/// The emails about reports and appeals (acknowledgement, outcome, appeal received and appeal decided), and to an
/// author whose pending post couldn't be published. Sent straight away through the Email module (no queue). A failed send is logged and reported back as false; it never undoes the
/// report or decision, which are already saved. Nothing is sent when there's no contact address.
/// </summary>
public sealed class ModerationNotifications(
    IEmailSender email,
    IOptions<EmailOptions> emailOptions,
    IOptions<PostsOptions> postsOptions,
    TimeProvider clock,
    ILogger<ModerationNotifications> logger)
{
    /// <summary>
    /// Emails a new report to the support inbox (<see cref="PostsOptions.ReportsInbox"/>) with a link to the content.
    /// False if it's turned off or couldn't be sent; the report is in the moderation queue either way.
    /// </summary>
    /// <param name="report">The saved report.</param>
    /// <param name="profileHandle">For a profile report: the @handle, for the link.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    public Task<bool> TellModeratorsAsync(Report report, string? profileHandle, CancellationToken cancellationToken)
    {
        var options = postsOptions.Value;
        var what = report.TargetType switch
        {
            ReportTargetType.Comment => "a comment",
            ReportTargetType.Profile => profileHandle is null ? "an account" : $"the account @{profileHandle}",
            ReportTargetType.Community => "a community",
            ReportTargetType.CommunityDiscussion => "a discussion in a community",
            ReportTargetType.CommunityReply => "a reply in a community discussion",
            _ => "a post",
        };
        var path = report.TargetType switch
        {
            ReportTargetType.Profile => profileHandle is null ? null : $"/u/{Uri.EscapeDataString(profileHandle)}",
            _ => report.PostId is { } postId ? $"/p/{postId}" : null,
        };
        var link = options.AppUrl is { } app && path is not null ? new Uri(app, path).ToString() : null;
        var reporter = report.Source == ReportSource.PublicForm
            ? $"{report.ReporterName} ({report.ContactEmail}), on the public form"
            : "a signed-in member";

        var template = EmailTemplates.ReportForModerators(
            report.Reference,
            what,
            ReportReasons.Describe(report.Reason),
            report.Priority == ReportPriority.Urgent,
            report.DueAt,
            report.Details,
            reporter,
            link);
        return SendAsync(options.ReportsInbox, template, report.Reference, cancellationToken);
    }

    /// <summary>Acknowledges a new report and marks it acknowledged. The caller saves the report.</summary>
    public async Task<bool> ReportReceivedAsync(Report report, CancellationToken cancellationToken)
    {
        var what = report.TargetType switch
        {
            ReportTargetType.Comment => "a comment",
            ReportTargetType.Profile => "a profile",
            ReportTargetType.Community => "a community",
            ReportTargetType.CommunityDiscussion => "a discussion",
            ReportTargetType.CommunityReply => "a reply",
            _ => "a post",
        };
        var urgent = report.Priority == ReportPriority.Urgent;
        var template = EmailTemplates.ReportReceived(
            report.Reference,
            what,
            urgent,
            urgent ? Report.UrgentResponseTime : Report.NormalResponseTime);

        if (await SendAsync(report.ContactEmail, template, report.Reference, cancellationToken))
        {
            report.MarkAcknowledged(clock.GetUtcNow());
            return true;
        }

        return false;
    }

    /// <summary>Tells the reporter the outcome and marks it notified. The caller saves the report.</summary>
    public async Task<bool> ReportResolvedAsync(Report report, CancellationToken cancellationToken)
    {
        var template = EmailTemplates.ReportResolved(report.Reference, report.Status == ReportStatus.ContentHidden);
        if (await SendAsync(report.ContactEmail, template, report.Reference, cancellationToken))
        {
            report.MarkOutcomeNotified(clock.GetUtcNow());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tells a post's author it couldn't be published because a photo or video failed, using the address saved with
    /// the pending post. False if there's no address or sending failed.
    /// </summary>
    public Task<bool> PostNotPublishedAsync(Post post, CancellationToken cancellationToken) =>
        SendAsync(
            post.NotifyEmail,
            EmailTemplates.PostNotPublished(post.Title, post.MediaFailureReason ?? "A photo or video couldn't be processed."),
            $"post {post.Id}",
            cancellationToken);

    public Task<bool> AppealReceivedAsync(Appeal appeal, CancellationToken cancellationToken) =>
        SendAsync(appeal.ContactEmail, EmailTemplates.AppealReceived(appeal.Reference, Appeal.ResponseTime), appeal.Reference, cancellationToken);

    public Task<bool> AppealDecidedAsync(Appeal appeal, CancellationToken cancellationToken) =>
        SendAsync(
            appeal.ContactEmail,
            EmailTemplates.AppealDecided(appeal.Reference, appeal.Status == AppealStatus.Overturned),
            appeal.Reference,
            cancellationToken);

    private async Task<bool> SendAsync(string? address, EmailTemplate template, string reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            await email.SendAsync(template.To(address, emailOptions.Value.LogoUrl), cancellationToken);
            return true;
        }
        catch (MessageDeliveryException ex)
        {
            logger.LogWarning(ex, "The email about {Reference} couldn't be sent.", reference);
            return false;
        }
    }
}
