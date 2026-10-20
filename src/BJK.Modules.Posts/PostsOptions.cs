using BJK.Modules.Posts.Domain;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts;

/// <summary>Posts module settings. Section <c>Posts</c>.</summary>
public sealed class PostsOptions
{
    public const string SectionName = "Posts";

    /// <summary>
    /// Where incidents may be posted, as region codes from <see cref="ServiceRegions"/>: <c>GB</c> (the UK, including
    /// Northern Ireland), <c>IM</c> (Isle of Man), <c>JE</c> (Jersey), <c>GG</c> (Guernsey, Alderney and Sark).
    /// Empty (the default) means the UK only.
    /// </summary>
    public string[] AllowedRegions { get; set; } = [];

    /// <summary>The regions in force: <see cref="AllowedRegions"/>, or the UK if none are set.</summary>
    public IReadOnlyList<string> EffectiveRegions => AllowedRegions.Length == 0 ? ServiceRegions.Default : AllowedRegions;
}

internal sealed class PostsOptionsValidator : IValidateOptions<PostsOptions>
{
    public ValidateOptionsResult Validate(string? name, PostsOptions options)
    {
        var unknown = options.AllowedRegions
            .Where(r => !ServiceRegions.Known.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return unknown.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Posts:AllowedRegions has unknown regions ({string.Join(", ", unknown)}). Known: {string.Join(", ", ServiceRegions.Known)}.");
    }
}

/// <summary>Reels settings. Section <c>Reels</c>.</summary>
public sealed class ReelsOptions
{
    public const string SectionName = "Reels";

    /// <summary>Plays a day (UK calendar day) by plan. Premium and Premium + are unlimited.</summary>
    public ReelDailyPlays DailyPlays { get; set; } = new();

    /// <summary>How often the ranking (<c>posts.Posts.ReelScore</c>) is recomputed. Default 10 minutes.</summary>
    public TimeSpan RankingRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Videos older than this get a score of 0 (they still appear, after newer ones). Default 30 days.</summary>
    public TimeSpan RankingWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long signed-in viewers' plays are kept. Default 35 days.</summary>
    public TimeSpan PlayRetention { get; set; } = TimeSpan.FromDays(35);

    /// <summary>How long logged-out viewers' plays (hashed viewer ids or IP addresses) are kept. Default 2 days.</summary>
    public TimeSpan AnonymousPlayRetention { get; set; } = TimeSpan.FromDays(2);

    /// <summary>How often old plays are deleted. Default 1 hour.</summary>
    public TimeSpan PlaySweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Key for hashing logged-out viewers' ids and IP addresses before they're stored (HMAC-SHA256). Optional; without
    /// it they're hashed with plain SHA-256, which a determined person could reverse for IP addresses. Set it in
    /// deployed environments. Changing it resets logged-out viewers' counts for the day. Secret.
    /// </summary>
    public string? ViewerKeySecret { get; set; }
}

/// <summary>Daily reel plays. Section <c>Reels:DailyPlays</c>.</summary>
public sealed class ReelDailyPlays
{
    /// <summary>Logged out. Default 10.</summary>
    public int Anonymous { get; set; } = 10;

    /// <summary>Free plan. Default 25.</summary>
    public int Free { get; set; } = 25;
}

internal sealed class ReelsOptionsValidator : IValidateOptions<ReelsOptions>
{
    public ValidateOptionsResult Validate(string? name, ReelsOptions options)
    {
        var errors = new List<string>();
        if (options.DailyPlays.Anonymous < 0 || options.DailyPlays.Free < 0)
        {
            errors.Add("Reels:DailyPlays:Anonymous and Free can't be negative.");
        }

        if (options.RankingRefreshInterval < TimeSpan.FromSeconds(30) || options.PlaySweepInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add("Reels:RankingRefreshInterval must be at least 30 seconds and Reels:PlaySweepInterval at least a minute.");
        }

        if (options.RankingWindow < TimeSpan.FromDays(1))
        {
            errors.Add("Reels:RankingWindow must be at least a day.");
        }

        if (options.AnonymousPlayRetention < TimeSpan.FromDays(1) || options.PlayRetention < TimeSpan.FromDays(1))
        {
            errors.Add("Reels:PlayRetention and AnonymousPlayRetention must be at least a day (today's plays are needed for the limits).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Incident heat map settings. Section <c>Heatmap</c>.</summary>
public sealed class HeatmapOptions
{
    public const string SectionName = "Heatmap";

    /// <summary>Cells (and area figures) with fewer incidents than this are hidden. Default 3; at least 2.</summary>
    public int MinCellCount { get; set; } = 3;

    /// <summary>
    /// The threshold when vehicle filters are used (type, make, model or colour), which narrow counts enough to risk
    /// pointing at one person's car. Default 5; never lower than <see cref="MinCellCount"/>.
    /// </summary>
    public int VehicleMinCellCount { get; set; } = 5;

    /// <summary>
    /// The model filter only applies when the map shows at least this many km each way (a town or wider). Default 10.
    /// </summary>
    public int ModelFilterMinSpanKm { get; set; } = 10;

    /// <summary>How far back each plan can look, in days.</summary>
    public HeatmapHistoryDays HistoryDays { get; set; } = new();

    /// <summary>The threshold for a request: higher with vehicle filters.</summary>
    /// <param name="request">The applied request.</param>
    public int MinCountFor(UseCases.Heatmap.HeatmapRequest request) =>
        request.HasVehicleFilters ? Math.Max(MinCellCount, VehicleMinCellCount) : MinCellCount;

    /// <summary>The most cells one response returns (the busiest). Default 10,000.</summary>
    public int MaxCells { get; set; } = 10_000;
}

/// <summary>History by plan, in days. Section <c>Heatmap:HistoryDays</c>.</summary>
public sealed class HeatmapHistoryDays
{
    /// <summary>Default 7 (the national teaser).</summary>
    public int Free { get; set; } = 7;

    /// <summary>Default 365 (12 months).</summary>
    public int Premium { get; set; } = 365;

    /// <summary>Default 1826 (5 years).</summary>
    public int PremiumPlus { get; set; } = 1826;
}

internal sealed class HeatmapOptionsValidator : IValidateOptions<HeatmapOptions>
{
    public ValidateOptionsResult Validate(string? name, HeatmapOptions options)
    {
        var errors = new List<string>();
        if (options.MinCellCount < 2)
        {
            errors.Add("Heatmap:MinCellCount must be at least 2, or single incidents could be pinpointed.");
        }

        var days = options.HistoryDays;
        if (days.Free < 1 || days.Premium < days.Free || days.PremiumPlus < days.Premium)
        {
            errors.Add("Heatmap:HistoryDays must be at least 1 and grow with the plan (Free ≤ Premium ≤ PremiumPlus).");
        }

        if (options.MaxCells < 100)
        {
            errors.Add("Heatmap:MaxCells must be at least 100.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
