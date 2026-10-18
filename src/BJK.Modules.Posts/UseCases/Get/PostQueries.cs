using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Get;

/// <summary>
/// One post. Open to anonymous callers. Hidden posts are only shown to their author, or to moderators through the
/// admin endpoint (<c>AsModerator</c>).
/// </summary>
public sealed record GetPostQuery(Guid PostId, bool AsModerator = false) : IRequest<PostDto>;

public sealed class GetPostQueryHandler(IPostRepository posts, PostViews views, ICurrentUser caller)
    : IRequestHandler<GetPostQuery, PostDto>
{
    public async Task<PostDto> Handle(GetPostQuery request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetVisibleAsync(posts, caller, request.PostId, cancellationToken, request.AsModerator);
        return await views.BuildAsync(post, caller.UserId(), cancellationToken);
    }
}

/// <summary>
/// The public feed, newest first. Open to anonymous callers. Optional filters: category, author's @handle,
/// and an area (a point plus a radius in km).
/// </summary>
public sealed record ListPostsQuery(
    IncidentCategory? Category = null,
    string? Author = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null,
    int Page = 1,
    int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListPostsQueryValidator : AbstractValidator<ListPostsQuery>
{
    public const int MaxRadiusKm = 200;

    public ListPostsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
        RuleFor(q => q.Category).IsInEnum();
        RuleFor(q => q.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(q => q.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(q => q.RadiusKm).InclusiveBetween(1, MaxRadiusKm);
        RuleFor(q => q)
            .Must(q => (q.Latitude is null) == (q.Longitude is null) && (q.Latitude is null) == (q.RadiusKm is null))
            .OverridePropertyName("Area")
            .WithMessage("To search an area, give latitude, longitude and radiusKm together.");
    }
}

public sealed class ListPostsQueryHandler(
    IPostRepository posts,
    IProfileDirectory profiles,
    PostViews views,
    ICurrentUser caller)
    : IRequestHandler<ListPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListPostsQuery request, CancellationToken cancellationToken)
    {
        Guid? authorId = null;
        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            var author = await profiles.FindByHandleAsync(request.Author, cancellationToken);
            if (author is null)
            {
                return new PagedResult<PostDto>([], request.Page, request.PageSize, 0);
            }

            authorId = author.UserId;
        }

        var area = request is { Latitude: { } lat, Longitude: { } lng, RadiusKm: { } radius }
            ? GeoBox.Around(lat, lng, radius)
            : null;

        var (items, total) = await posts.SearchAsync(
            new PostSearch(authorId, request.Category, area, IncludeHidden: false),
            request.Page,
            request.PageSize,
            cancellationToken);

        return new PagedResult<PostDto>(
            await views.BuildAsync(items, caller.UserId(), cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}

/// <summary>The signed-in user's own posts, newest first, including ones an admin has hidden.</summary>
public sealed record ListMyPostsQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListMyPostsQueryValidator : AbstractValidator<ListMyPostsQuery>
{
    public ListMyPostsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListMyPostsQueryHandler(IPostRepository posts, PostViews views, ICurrentUser caller)
    : IRequestHandler<ListMyPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListMyPostsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var (items, total) = await posts.SearchAsync(
            new PostSearch(userId, Category: null, Area: null, IncludeHidden: true),
            request.Page,
            request.PageSize,
            cancellationToken);

        return new PagedResult<PostDto>(
            await views.BuildAsync(items, userId, cancellationToken),
            request.Page,
            request.PageSize,
            total);
    }
}
