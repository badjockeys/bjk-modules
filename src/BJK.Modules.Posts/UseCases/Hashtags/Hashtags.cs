using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Comments;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Hashtags;

/// <summary>
/// Keeps a post's hashtag links in step with its title and description. Called when a post is created or edited,
/// before saving (the caller saves). New tags are created; removed ones are unlinked. Tags on the configured
/// blocklist (<c>Hashtags:Blocklist</c>), offensive tags and number plates are never recorded.
/// </summary>
public sealed class HashtagSync(IHashtagRepository hashtags, IOptions<HashtagOptions> options, TimeProvider clock)
{
    public async Task SyncAsync(Post post, CancellationToken cancellationToken)
    {
        var blocked = options.Value.Blocklist.Select(HashtagParser.KeyOf).ToHashSet(StringComparer.Ordinal);
        var wanted = HashtagParser.Extract(post.Title, post.Description)
            .Where(t => !blocked.Contains(HashtagParser.KeyOf(t)))
            .ToList();
        var wantedKeys = wanted.Select(HashtagParser.KeyOf).ToList();

        var links = await hashtags.GetLinksAsync(post.Id, cancellationToken);
        var existing = (await hashtags.GetByKeysAsync(wantedKeys, cancellationToken)).ToDictionary(h => h.Key, StringComparer.Ordinal);
        var linkedIds = links.Select(l => l.HashtagId).ToHashSet();

        var now = clock.GetUtcNow();
        var keepIds = new HashSet<Guid>();
        foreach (var tag in wanted)
        {
            var key = HashtagParser.KeyOf(tag);
            if (!existing.TryGetValue(key, out var hashtag))
            {
                hashtag = Hashtag.Create(tag, now);
                hashtags.Add(hashtag);
                existing[key] = hashtag;
            }

            keepIds.Add(hashtag.Id);
            if (!linkedIds.Contains(hashtag.Id))
            {
                hashtags.Add(new PostHashtag(post.Id, hashtag.Id, post.AuthorId, post.CreatedAt));
                hashtag.Used();
            }
        }

        var removed = links.Where(l => !keepIds.Contains(l.HashtagId)).ToList();
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(l => l.HashtagId).ToHashSet();
            foreach (var link in removed)
            {
                hashtags.Remove(link);
            }

            // Their tags count one use fewer.
            foreach (var hashtag in await hashtags.GetByIdsAsync(removedIds, cancellationToken))
            {
                hashtag.Unused();
            }
        }
    }
}

/// <summary>A suggestion as # is typed: the tag as first written and how many posts used it.</summary>
public sealed record HashtagSuggestionDto(string Tag, int Uses);

/// <summary>Tags starting with what's been typed after the #, most used first (up to <c>Limit</c>, at most 10).</summary>
public sealed record SuggestHashtagsQuery(string? Prefix, int Limit = 8) : IRequest<IReadOnlyList<HashtagSuggestionDto>>;

public sealed class SuggestHashtagsQueryValidator : AbstractValidator<SuggestHashtagsQuery>
{
    public SuggestHashtagsQueryValidator()
    {
        RuleFor(q => q.Prefix)
            .Must(HashtagParser.IsPrefix)
            .WithMessage("Type a letter, then letters, numbers or underscores.");
        RuleFor(q => q.Limit).InclusiveBetween(1, 10);
    }
}

public sealed class SuggestHashtagsQueryHandler(IHashtagRepository hashtags)
    : IRequestHandler<SuggestHashtagsQuery, IReadOnlyList<HashtagSuggestionDto>>
{
    public async Task<IReadOnlyList<HashtagSuggestionDto>> Handle(SuggestHashtagsQuery request, CancellationToken cancellationToken) =>
        (await hashtags.SuggestAsync(HashtagParser.KeyOf(request.Prefix!), request.Limit, cancellationToken))
            .Select(h => new HashtagSuggestionDto(h.Display, h.TotalUses))
            .ToList();
}

/// <summary>A hashtag for its page: as first written, and how many posts have used it.</summary>
public sealed record HashtagDto(string Tag, string Key, int Uses, DateTimeOffset FirstUsedAt);

/// <summary>A hashtag by name (with or without the #, any case). 404 if no post has used it.</summary>
public sealed record GetHashtagQuery(string Tag) : IRequest<HashtagDto>;

public sealed class GetHashtagQueryHandler(IHashtagRepository hashtags) : IRequestHandler<GetHashtagQuery, HashtagDto>
{
    public async Task<HashtagDto> Handle(GetHashtagQuery request, CancellationToken cancellationToken)
    {
        var key = HashtagParser.KeyOf(request.Tag ?? string.Empty);
        var hashtag = HashtagParser.IsPrefix(key) ? await hashtags.FindAsync(key, cancellationToken) : null;
        if (hashtag is null || hashtag.TotalUses == 0)
        {
            throw new NotFoundException("Hashtag", request.Tag ?? string.Empty);
        }

        return new HashtagDto(hashtag.Display, hashtag.Key, hashtag.TotalUses, hashtag.FirstUsedAt);
    }
}

/// <summary>A trending tag: as first written, and its public posts in the last 24 hours.</summary>
public sealed record TrendingTagDto(string Tag, int Posts);

/// <summary>A trending town, e.g. "Prescot", and its public posts in the last 24 hours.</summary>
public sealed record TrendingPlaceDto(string Name, int Posts);

/// <summary>What's trending: tags and places growing fastest against their usual level.</summary>
public sealed record TrendingDto(IReadOnlyList<TrendingTagDto> Hashtags, IReadOnlyList<TrendingPlaceDto> Places);

/// <summary>
/// Trending (anyone): the hashtags and towns with the most public posts in the last 24 hours compared with their daily
/// average over the week before. Each needs at least <c>Hashtags:TrendingMinPosts</c> posts from
/// <c>Hashtags:TrendingMinAuthors</c> different people. Hidden, held and deleted posts never count; moderators can hide
/// a tag from Trending.
/// </summary>
public sealed record GetTrendingQuery : IRequest<TrendingDto>;

public sealed class GetTrendingQueryHandler(IHashtagRepository hashtags, IOptions<HashtagOptions> options, TimeProvider clock)
    : IRequestHandler<GetTrendingQuery, TrendingDto>
{
    public async Task<TrendingDto> Handle(GetTrendingQuery request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var dayAgo = now.AddHours(-24);
        var baselineFrom = dayAgo.AddDays(-settings.BaselineDays);

        var tags = await hashtags.TrendingTagsAsync(
            dayAgo, baselineFrom, settings.BaselineDays, settings.TrendingMinPosts, settings.TrendingMinAuthors, settings.TrendingTags, cancellationToken);
        var places = settings.TrendingPlaces == 0
            ? []
            : await hashtags.TrendingPlacesAsync(
                dayAgo, baselineFrom, settings.BaselineDays, settings.TrendingMinPosts, settings.TrendingMinAuthors, settings.TrendingPlaces, cancellationToken);

        return new TrendingDto(
            tags.Select(t => new TrendingTagDto(t.Name, t.Posts)).ToList(),
            places.Select(p => new TrendingPlaceDto(p.Name, p.Posts)).ToList());
    }
}

/// <summary>Moderators: hide a tag from Trending and suggestions, or show it again. A reason is required (audited).</summary>
public sealed record SetHashtagHiddenCommand(string Tag, bool Hidden, string? Reason) : IRequest<HashtagDto>;

public sealed class SetHashtagHiddenCommandValidator : AbstractValidator<SetHashtagHiddenCommand>
{
    public SetHashtagHiddenCommandValidator()
    {
        RuleFor(c => c.Reason).ModerationReason();
    }
}

public sealed class SetHashtagHiddenCommandHandler(IHashtagRepository hashtags, IModerationAuditLog audit, ICurrentUser caller)
    : IRequestHandler<SetHashtagHiddenCommand, HashtagDto>
{
    public async Task<HashtagDto> Handle(SetHashtagHiddenCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var key = HashtagParser.KeyOf(request.Tag ?? string.Empty);
        var hashtag = (await hashtags.GetByKeysAsync([key], cancellationToken)).FirstOrDefault()
            ?? throw new NotFoundException("Hashtag", request.Tag ?? string.Empty);

        var before = hashtag.IsHiddenFromTrends;
        hashtag.SetHiddenFromTrends(request.Hidden);
        await audit.RecordAsync(
            PostsAudit.Entry(
                request.Hidden ? ModerationActions.HideHashtag : ModerationActions.ShowHashtag,
                "Hashtag",
                hashtag.Id,
                request.Reason,
                $"#{hashtag.Display} HiddenFromTrends={before}",
                $"#{hashtag.Display} HiddenFromTrends={hashtag.IsHiddenFromTrends}"),
            cancellationToken);
        await hashtags.SaveChangesAsync(cancellationToken);
        return new HashtagDto(hashtag.Display, hashtag.Key, hashtag.TotalUses, hashtag.FirstUsedAt);
    }
}
