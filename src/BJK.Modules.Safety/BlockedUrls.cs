using System.Text.RegularExpressions;

namespace BJK.Modules.Safety;

/// <summary>
/// Web addresses that must not be shared (<c>posts.BlockedUrls</c> in SQL: from a list such as the IWF URL List, or
/// added by moderators). Text containing one is held for review by <see cref="IContentScreener"/>. Implemented by the
/// Posts module, which keeps the list in memory; with no implementation registered nothing is checked.
/// </summary>
public interface IBlockedUrlList
{
    /// <summary>The first listed address found in the text, or null.</summary>
    /// <param name="text">Post, comment or other text.</param>
    string? FindIn(string text);
}

/// <summary>Finding web addresses in text and matching them against list entries.</summary>
public static partial class BlockedUrls
{
    /// <summary>The screening label for text holding a blocked address.</summary>
    public const string Label = "blocked_url";

    /// <summary>
    /// Addresses in text, normalised (see <see cref="Normalise"/>): with or without http(s)://, so "example.com/x"
    /// and "https://www.example.com/x" are both found.
    /// </summary>
    /// <param name="text">The text.</param>
    public static IEnumerable<string> Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in UrlPattern().Matches(text))
        {
            if (Normalise(match.Value) is { } url)
            {
                yield return url;
            }
        }
    }

    /// <summary>
    /// An address as host and path, lower case: no scheme, no "www.", no port, query or fragment, no trailing
    /// slash, e.g. "example.com/some/page". Null if it isn't an address.
    /// </summary>
    /// <param name="value">An address or list entry.</param>
    public static string? Normalise(string? value)
    {
        var text = value?.Trim().TrimEnd('.', ',', ')', ']', '!', '?', ';', ':', '"', '\'');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.'))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        return host + path;
    }

    /// <summary>
    /// True if the address is the entry, under it (a path inside it), or, for an entry that's only a host, on a
    /// subdomain of it.
    /// </summary>
    /// <param name="entry">A normalised list entry.</param>
    /// <param name="url">A normalised address.</param>
    public static bool Matches(string entry, string url)
    {
        if (url == entry || url.StartsWith(entry + "/", StringComparison.Ordinal))
        {
            return true;
        }

        if (entry.Contains('/'))
        {
            return false;
        }

        var host = url.Split('/', 2)[0];
        return host.EndsWith("." + entry, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?:https?://)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?::\d{1,5})?(?:/[^\s<>""']*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}

/// <summary>Holds text that contains a blocked address for review, whatever the inner screener says.</summary>
internal sealed class BlockedUrlScreener(IContentScreener inner, IBlockedUrlList list) : IContentScreener
{
    public async Task<ScreeningResult> ScreenAsync(string text, CancellationToken cancellationToken)
    {
        var result = await inner.ScreenAsync(text, cancellationToken);
        return list.FindIn(text) is null
            ? result
            : new ScreeningResult(1, [.. result.Labels.Append(BlockedUrls.Label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], ScreeningVerdict.Hold);
    }
}
