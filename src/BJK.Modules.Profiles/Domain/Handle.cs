using System.Text.RegularExpressions;

namespace BJK.Modules.Profiles.Domain;

/// <summary>
/// Rules for @handles: 3 to 30 characters, lower-case letters, digits and underscores, starting with a letter.
/// Handles are stored lower-case, so "@RoadHog" and "@roadhog" are the same handle.
/// </summary>
public static partial class Handle
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "about", "admin", "administrator", "api", "app", "badjockey", "badjockeys", "bad_jockeys", "help",
        "login", "logout", "me", "media", "mod", "moderator", "null", "official", "posts", "privacy",
        "profiles", "register", "root", "security", "settings", "signin", "signup", "staff", "support",
        "system", "terms", "undefined", "users",
    };

    /// <summary>Trims, removes a leading @ and lower-cases. Doesn't check the rules.</summary>
    public static string Normalise(string? handle) =>
        (handle ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Checks a normalised handle. Returns null if it's allowed, otherwise the reason it isn't.</summary>
    public static string? Problem(string normalisedHandle)
    {
        if (normalisedHandle.Length < MinLength || normalisedHandle.Length > MaxLength)
        {
            return $"Handles are {MinLength} to {MaxLength} characters.";
        }

        if (!Pattern().IsMatch(normalisedHandle))
        {
            return "Handles start with a letter and use only letters, numbers and underscores.";
        }

        return Reserved.Contains(normalisedHandle) ? "That handle is reserved." : null;
    }

    public static bool IsValid(string normalisedHandle) => Problem(normalisedHandle) is null;

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
