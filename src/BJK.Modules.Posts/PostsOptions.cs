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
