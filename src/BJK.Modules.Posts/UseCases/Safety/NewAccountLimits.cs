using System.Globalization;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Safety;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Safety;

/// <summary>
/// Rate limits for new accounts (<see cref="SafetyOptions.NewAccounts"/>): while a profile is younger than the
/// period, it can post and comment only a few times in any rolling period. Older accounts aren't limited here. Also
/// pauses posting for people whose posts keep being removed as not about driving (<see cref="SafetyOptions.OffTopic"/>).
/// </summary>
public sealed class NewAccountLimits(
    IProfileDirectory profiles,
    IPostRepository posts,
    ICommentRepository comments,
    IOptions<SafetyOptions> options,
    TimeProvider clock)
{
    public const string Code = "new_account_limit";

    public const string PausedCode = "posting_paused";

    /// <summary>
    /// A 429 if the user's account is new and has already posted the maximum, or if their posting is paused because
    /// posts were removed as not about driving.
    /// </summary>
    public async Task EnsureCanPostAsync(Guid userId, CancellationToken cancellationToken)
    {
        await EnsureNotPausedAsync(userId, cancellationToken);
        var limits = options.Value.NewAccounts;
        if (!await IsNewAsync(userId, null, cancellationToken))
        {
            return;
        }

        var since = clock.GetUtcNow() - limits.Period;
        if (await posts.CountCreatedSinceAsync(userId, since, cancellationToken) >= limits.MaxPosts)
        {
            throw new TooManyRequestsException(
                $"New accounts can post {limits.MaxPosts} times in their first {Hours(limits.Period)}. Please try again later.",
                Code);
        }
    }

    /// <summary>A 429 if the user's account is new and has already commented the maximum.</summary>
    public async Task EnsureCanCommentAsync(Guid userId, ProfileSummary? profile, CancellationToken cancellationToken)
    {
        var limits = options.Value.NewAccounts;
        if (!await IsNewAsync(userId, profile, cancellationToken))
        {
            return;
        }

        var since = clock.GetUtcNow() - limits.Period;
        if (await comments.CountCreatedSinceAsync(userId, since, cancellationToken) >= limits.MaxComments)
        {
            throw new TooManyRequestsException(
                $"New accounts can comment {limits.MaxComments} times in their first {Hours(limits.Period)}. Please try again later.",
                Code);
        }
    }

    /// <summary>
    /// A 429 while posting is paused: <c>RemovalsToPause</c> posts removed as off-topic within <c>Window</c> pause
    /// posting until <c>Pause</c> after the latest removal (owner, 23 Oct 2026).
    /// </summary>
    public async Task EnsureNotPausedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rules = options.Value.OffTopic;
        if (rules.RemovalsToPause <= 0 || rules.Pause <= TimeSpan.Zero)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var (count, latest) = await posts.GetOffTopicRemovalsSinceAsync(userId, now - rules.Window, cancellationToken);
        if (count >= rules.RemovalsToPause && latest is { } last && last + rules.Pause > now)
        {
            var until = (last + rules.Pause).ToOffset(TimeSpan.Zero).ToString("d MMMM", CultureInfo.InvariantCulture);
            throw new TooManyRequestsException(
                $"Your posting is paused until {until} because {count} of your posts were removed as not about driving. Bad Jockeys is only for bad driving caught on camera.",
                PausedCode);
        }
    }

    private async Task<bool> IsNewAsync(Guid userId, ProfileSummary? profile, CancellationToken cancellationToken)
    {
        var period = options.Value.NewAccounts.Period;
        if (period <= TimeSpan.Zero)
        {
            return false;
        }

        if (profile is null)
        {
            var found = await profiles.GetSummariesAsync([userId], cancellationToken);
            profile = found.GetValueOrDefault(userId);
        }

        return profile is not null && profile.CreatedAt > clock.GetUtcNow() - period;
    }

    private static string Hours(TimeSpan period) =>
        period.TotalHours == 1 ? "hour" : $"{period.TotalHours:0.#} hours";
}
