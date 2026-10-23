using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Follows;

/// <summary>How someone suggested for tagging is connected to you.</summary>
public enum TagConnection
{
    /// <summary>Not connected: another member whose name matches.</summary>
    None,

    /// <summary>You follow each other.</summary>
    Mutual,

    /// <summary>They follow you.</summary>
    FollowsYou,

    /// <summary>You follow them.</summary>
    YouFollow,
}

/// <summary>Someone to tag, with how they're connected to you.</summary>
public sealed record TagSuggestionDto(Guid Id, string Handle, string DisplayName, Uri? AvatarUrl, TagConnection Connection);

/// <summary>
/// People to tag as you type (owner, 23 Oct 2026: "like Facebook and Instagram"): those whose @handle or name (any
/// word of it) starts with what you've typed. People you're connected to come first (following each other, then your
/// followers, then people you follow); then other members, adults only, whose handle or name starts with it. Never
/// you, and never anyone blocked either way. Under-18s only get people they're connected to.
/// </summary>
public sealed record TagSuggestionsQuery(string Text, int Limit = 8) : IRequest<IReadOnlyList<TagSuggestionDto>>;

public sealed class TagSuggestionsQueryValidator : AbstractValidator<TagSuggestionsQuery>
{
    public TagSuggestionsQueryValidator()
    {
        RuleFor(q => q.Text).NotNull().MaximumLength(TagSuggestionsQueryHandler.MaxTextLength);
        RuleFor(q => q.Limit).InclusiveBetween(1, 20);
    }
}

public sealed class TagSuggestionsQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser caller)
    : IRequestHandler<TagSuggestionsQuery, IReadOnlyList<TagSuggestionDto>>
{
    public const int MaxTextLength = 40;

    public async Task<IReadOnlyList<TagSuggestionDto>> Handle(TagSuggestionsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var text = Clean(request.Text);
        if (text.Length == 0)
        {
            return [];
        }

        var blocked = (await profiles.GetBlockedEitherWayAsync(userId, cancellationToken)).ToHashSet();
        // A few extra, so people blocked either way can be left out and still fill the list.
        var take = request.Limit + blocked.Count;
        var connected = (await profiles.SearchConnectionsAsync(userId, text, take, cancellationToken))
            .Where(c => !blocked.Contains(c.Profile.Id))
            .Take(request.Limit)
            .ToList();

        var people = connected.Select(c => (c.Profile, Connection: c.FollowsYou && c.YouFollow
            ? TagConnection.Mutual
            : c.FollowsYou ? TagConnection.FollowsYou : TagConnection.YouFollow)).ToList();

        if (people.Count < request.Limit && !caller.IsMinor)
        {
            var exclude = people.Select(p => p.Profile.Id).Append(userId).ToList();
            var others = await profiles.SearchAdultsAsync(text, exclude, request.Limit - people.Count + blocked.Count, cancellationToken);
            people.AddRange(others.Where(p => !blocked.Contains(p.Id)).Take(request.Limit - people.Count).Select(p => (p, TagConnection.None)));
        }

        var avatars = await Avatars.GetUrlsAsync(media, people.Select(p => p.Profile).ToList(), cancellationToken);
        return people
            .Select(p => new TagSuggestionDto(p.Profile.Id, p.Profile.Handle, p.Profile.DisplayName, Avatars.UrlFor(p.Profile, avatars), p.Connection))
            .ToList();
    }

    /// <summary>"@Aa" → "Aa": letters, digits, underscores, spaces, dots, apostrophes and hyphens only.</summary>
    internal static string Clean(string? text)
    {
        var value = (text ?? string.Empty).Trim().TrimStart('@');
        var kept = new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or ' ' or '.' or '\'' or '-').ToArray()).Trim();
        return kept.Length <= MaxTextLength ? kept : kept[..MaxTextLength];
    }
}
