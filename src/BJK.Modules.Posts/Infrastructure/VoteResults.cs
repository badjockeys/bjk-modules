using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Announces closed votes: every 5 minutes, posts whose vote has closed get their result sent to the author and the
/// post's followers (VoteClosed notifications), once. Posts that aren't public by then are marked done silently.
/// Safe on several replicas: a post is marked before anyone is told, and a clash on the row version skips it.
/// </summary>
internal sealed class VoteResults(IServiceScopeFactory scopes, TimeProvider clock, ILogger<VoteResults> logger) : BackgroundService
{
    public const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var announcer = scope.ServiceProvider.GetRequiredService<VoteResultAnnouncer>();
                await announcer.AnnounceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Announcing closed votes failed; trying again at the next interval.");
            }

            await Delay(Interval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken cancellationToken) =>
        Task.Delay(wait, clock, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
}

/// <summary>One pass of <see cref="VoteResults"/>.</summary>
public sealed class VoteResultAnnouncer(
    IEngagementRepository engagement,
    TimeProvider clock,
    ILogger<VoteResultAnnouncer> logger,
    INotificationSender? notifications = null)
{
    /// <summary>The most people told about one result (the author, then the oldest followers).</summary>
    public const int MaxRecipients = 500;

    /// <summary>Announces up to a batch of closed votes. Returns how many posts were handled.</summary>
    public async Task<int> AnnounceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await engagement.ListVotesToAnnounceAsync(now, VoteResults.BatchSize, cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var post in due)
        {
            post.MarkVoteResultSent(now);
        }

        try
        {
            await engagement.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Another replica (or an edit) got there first; whatever is left is picked up next time.
            logger.LogInformation("Some closed votes changed while being announced; retrying next time.");
            return 0;
        }

        if (notifications is null)
        {
            return due.Count;
        }

        var tallies = await engagement.GetVoteTalliesAsync(due.Select(p => p.Id).ToList(), null, cancellationToken);
        foreach (var post in due.Where(p => p.IsPublic))
        {
            var detail = Describe(tallies.GetValueOrDefault(post.Id) ?? VoteTally.Empty);
            var recipients = new List<Guid> { post.AuthorId };
            recipients.AddRange(await engagement.ListFollowerIdsAsync(post.Id, MaxRecipients, cancellationToken));
            foreach (var userId in recipients.Distinct().Take(MaxRecipients))
            {
                await notifications.NotifyAsync(
                    new NotificationRequest(userId, NotificationType.VoteClosed, PostId: post.Id, PostTitle: post.Title, Detail: detail),
                    cancellationToken);
            }
        }

        return due.Count;
    }

    /// <summary>The result in one sentence, e.g. "Most said the other road user was at fault (62% of 41 votes)."</summary>
    public static string Describe(VoteTally tally)
    {
        if (tally.Total == 0)
        {
            return "Nobody voted.";
        }

        var (choice, count) = tally.Counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).First();
        var tied = tally.Counts.Count(c => c.Value == count) > 1;
        var percent = (int)Math.Round(100.0 * count / tally.Total, MidpointRounding.AwayFromZero);
        var votes = tally.Total == 1 ? "1 vote" : $"{tally.Total} votes";
        if (tied)
        {
            return $"It was a tie ({votes}).";
        }

        var verdict = choice switch
        {
            VoteChoice.FilmingDriver => "the driver filming was at fault",
            VoteChoice.OtherRoadUser => "the other road user was at fault",
            VoteChoice.Both => "both were at fault",
            VoteChoice.Nobody => "nobody was really at fault",
            VoteChoice.Minor => "it was minor",
            VoteChoice.Careless => "it was careless",
            VoteChoice.Dangerous => "it was dangerous",
            _ => "it was shocking",
        };
        return $"Most said {verdict} ({percent}% of {votes}).";
    }
}
