using System.Text.RegularExpressions;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Shared.Notifications;

namespace BJK.Modules.Chat.UseCases;

/// <summary>
/// Example questions for the empty chat, the Free preview and the guard's replies, tailored to the member's home area:
/// a town gets questions that name it ("How many incidents have there been in Prescot this month?"), a postcode
/// district ("WA10") gets "near me" questions (answered with the home area as <see cref="NearPlace"/>), and members
/// without a home area, or logged-out callers, get questions that don't depend on a place.
/// </summary>
public static partial class ChatExamples
{
    /// <summary>For members without a home area, and logged-out callers.</summary>
    public static readonly IReadOnlyList<string> Untailored =
    [
        "Which towns have had the most incidents this month?",
        "Is school-run parking getting better?",
        "What time of day is worst for speeding?",
        "What's the most reported kind of bad driving this year?",
    ];

    /// <summary>The examples for a member's home area (null: <see cref="Untailored"/>). Local questions come first.</summary>
    public static IReadOnlyList<string> For(HomeArea? home)
    {
        if (TownOf(home) is { } town)
        {
            return
            [
                $"How many incidents have there been in {town} this month?",
                $"Worst roads for tailgating in {town}",
                $"Is pavement parking getting worse in {town}?",
                "What time of day is worst for speeding?",
            ];
        }

        if (home is not null && IsPostcodeDistrict(home.PlaceName.Trim()))
        {
            return
            [
                "How many incidents have there been near me this month?",
                "Worst roads for tailgating near me",
                "Is pavement parking getting worse near me?",
                "What time of day is worst for speeding?",
            ];
        }

        return Untailored;
    }

    /// <summary>
    /// Where "near me" means when the app doesn't send a place: the home area's position (about 1 km), named only when
    /// it's a town, since incidents' place names end in a town, not a postcode district.
    /// </summary>
    public static NearPlace? NearOf(HomeArea? home) =>
        home is null ? null : new NearPlace((double)home.Latitude, (double)home.Longitude, TownOf(home));

    /// <summary>The home area's name when it's a town or village, or null (no home area, or a postcode district).</summary>
    internal static string? TownOf(HomeArea? home)
    {
        var name = home?.PlaceName.Trim();
        return string.IsNullOrEmpty(name) || IsPostcodeDistrict(name) ? null : name;
    }

    /// <summary>True for a UK postcode district (outward code) such as "WA10", "L34" or "EC1A".</summary>
    internal static bool IsPostcodeDistrict(string name) => PostcodeDistrict().IsMatch(name);

    [GeneratedRegex(@"^[A-Z]{1,2}[0-9][A-Z0-9]?$", RegexOptions.IgnoreCase)]
    private static partial Regex PostcodeDistrict();
}
