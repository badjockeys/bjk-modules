using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Reactions;

/// <summary>
/// Who reacted to a post (owner, 22 Oct 2026): signed-in members only. <c>Type</c> filters to one reaction and
/// <c>Search</c> matches the display name or @handle. Under-18s (and anyone whose age isn't known) are counted but never
/// named; people blocked either way are left out of the counts and the list.
/// </summary>
public sealed record ListReactionsQuery(
    Guid PostId,
    ReactionType? Type = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 30) : IRequest<PostReactionsDto>;

/// <summary>One person who reacted.</summary>
public sealed record ReactorDto(Guid UserId, string Handle, string DisplayName, Uri? AvatarUrl, ReactionType Type);

/// <summary>
/// The people who reacted (newest first) and the totals per reaction. <c>UnnamedCount</c> is how many reactions in
/// the current filter come from people who aren't listed (under-18s, or no profile), shown as "and N others".
/// </summary>
public sealed record PostReactionsDto(
    PagedResult<ReactorDto> People,
    IReadOnlyDictionary<ReactionType, int> Counts,
    int Total,
    int UnnamedCount);

public sealed class ListReactionsQueryValidator : AbstractValidator<ListReactionsQuery>
{
    public ListReactionsQueryValidator()
    {
        RuleFor(q => q.Type).IsInEnum().When(q => q.Type is not null);
        RuleFor(q => q.Search).MaximumLength(50);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListReactionsQueryHandler(
    IPostRepository posts,
    IReactionRepository reactions,
    IProfileDirectory profiles,
    IBlockDirectory blocks,
    ICurrentUser caller)
    : IRequestHandler<ListReactionsQuery, PostReactionsDto>
{
    /// <summary>The most reactions looked at for one post. Counts on the post itself stay exact.</summary>
    public const int MaxReactions = 10_000;

    public async Task<PostReactionsDto> Handle(ListReactionsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var post = await PostAccess.GetPublishedAsync(posts, request.PostId, cancellationToken);
        await PostAccess.EnsureNotBlockedByAuthorAsync(blocks, post, userId, cancellationToken);

        var blocked = (await blocks.GetBlockedEitherWayAsync(userId, cancellationToken)).ToHashSet();
        var all = (await reactions.ListForPostAsync(post.Id, MaxReactions, cancellationToken))
            .Where(r => !blocked.Contains(r.UserId))
            .ToList();

        var counts = all.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count());
        var filtered = request.Type is { } type ? all.Where(r => r.Type == type).ToList() : all;

        var summaries = await profiles.GetSummariesAsync(filtered.Select(r => r.UserId).Distinct().ToList(), cancellationToken);
        var named = filtered
            .Where(r => summaries.TryGetValue(r.UserId, out var p) && p.IsAdult)
            .Select(r => (Reaction: r, Profile: summaries[r.UserId]))
            .ToList();
        var unnamed = filtered.Count - named.Count;

        var search = request.Search?.Trim().TrimStart('@');
        if (!string.IsNullOrEmpty(search))
        {
            named = named
                .Where(x => x.Profile.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || x.Profile.Handle.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var page = named
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new ReactorDto(x.Profile.UserId, x.Profile.Handle, x.Profile.DisplayName, x.Profile.AvatarUrl, x.Reaction.Type))
            .ToList();

        return new PostReactionsDto(
            new PagedResult<ReactorDto>(page, request.Page, request.PageSize, named.Count),
            counts,
            all.Count,
            string.IsNullOrEmpty(search) ? unnamed : 0);
    }
}
