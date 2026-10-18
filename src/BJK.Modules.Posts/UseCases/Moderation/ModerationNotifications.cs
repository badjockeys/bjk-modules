using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Email.Templates;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Shared.Delivery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Moderation;

/// <summary>
/// The emails about reports and appeals: acknowledgement, outcome, appeal received and appeal decided. Sent straight
/// away through the Email module (no queue). A failed send is logged and reported back as false; it never undoes the
/// report or decision, which are already saved. Nothing is sent when there's no contact address.
/// </summary>
public sealed class ModerationNotifications(
    IEmailSender email,
    IOptions<EmailOptions> emailOptions,
    TimeProvider clock,
    ILogger<ModerationNotifications> logger)
{
    /// <summary>Acknowledges a new report and marks it acknowledged. The caller saves the report.</summary>
    public async Task<bool> ReportReceivedAsync(Report report, CancellationToken cancellationToken)
    {
        var what = report.TargetType switch
        {
            ReportTargetType.Comment => "a comment",
            ReportTargetType.Profile => "a profile",
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
