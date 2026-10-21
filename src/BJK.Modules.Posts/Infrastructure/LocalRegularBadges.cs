using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.Infrastructure;

/// <summary>
/// Once a day, gives the Local regular badge to members who posted inside their home area (within its radius) in at
/// least 3 different months of the last 6. The badge is kept once given; moderators can remove it.
/// </summary>
internal sealed class LocalRegularBadges(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LocalRegularBadges> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LocalRegularFinder>().AwardAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Giving Local regular badges failed; trying again tomorrow.");
            }

            await Delay(Interval, stoppingToken);
        }
    }

    private Task Delay(TimeSpan wait, CancellationToken cancellationToken) =>
        Task.Delay(wait, clock, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
}

/// <summary>One pass of <see cref="LocalRegularBadges"/>.</summary>
public sealed class LocalRegularFinder(
    PostsDbContext db,
    IHomeAreas homeAreas,
    TimeProvider clock,
    IProfileBadges? badges = null)
{
    /// <summary>Months with a local post needed, out of the last <see cref="WindowMonths"/>.</summary>
    public const int MonthsNeeded = 3;

    public const int WindowMonths = 6;

    /// <summary>The most members checked in one pass.</summary>
    public const int MaxCandidates = 2000;

    /// <summary>Finds and awards. Returns the members who qualified.</summary>
    public async Task<IReadOnlyList<Guid>> AwardAsync(CancellationToken cancellationToken)
    {
        if (badges is null)
        {
            return [];
        }

        var since = clock.GetUtcNow().AddMonths(-WindowMonths);
        var recent = db.Posts.AsNoTracking()
            .Where(p => p.CreatedAt >= since
                && p.Status == PostStatus.Published
                && p.MediaState == PostMediaState.Ready
                && !p.IsAuthorDeactivated);

        // Only authors with posts in enough different months can qualify, wherever they posted.
        var candidates = await recent
            .GroupBy(p => new { p.AuthorId, p.CreatedAt.Year, p.CreatedAt.Month })
            .Select(g => g.Key)
            .GroupBy(k => k.AuthorId)
            .Where(g => g.Count() >= MonthsNeeded)
            .Select(g => g.Key)
            .Take(MaxCandidates)
            .ToListAsync(cancellationToken);

        var qualified = new List<Guid>();
        foreach (var authorId in candidates)
        {
            var home = await homeAreas.GetAsync(authorId, cancellationToken);
            if (home is null)
            {
                continue;
            }

            var posts = await recent
                .Where(p => p.AuthorId == authorId)
                .Select(p => new { p.Latitude, p.Longitude, p.CreatedAt })
                .ToListAsync(cancellationToken);
            var localMonths = posts
                .Where(p => GeoDistance.Km(home.Latitude, home.Longitude, p.Latitude, p.Longitude) <= home.RadiusKm)
                .Select(p => (p.CreatedAt.Year, p.CreatedAt.Month))
                .Distinct()
                .Count();
            if (localMonths >= MonthsNeeded)
            {
                qualified.Add(authorId);
            }
        }

        await badges.AwardLocalRegularAsync(qualified, cancellationToken);
        return qualified;
    }
}
