using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Notifications;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Timelines;

/// <summary>
/// The Following timeline: posts by the people the signed-in user follows, and their own, newest first. People blocked
/// either way are left out.
/// </summary>
public sealed record ListFollowingPostsQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListFollowingPostsQueryValidator : AbstractValidator<ListFollowingPostsQuery>
{
    public ListFollowingPostsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListFollowingPostsQueryHandler(
    IPostRepository posts,
    IFollowDirectory follows,
    IBlockDirectory blocks,
    PostViews views,
    ICurrentUser caller)
    : IRequestHandler<ListFollowingPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListFollowingPostsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var blocked = (await blocks.GetBlockedEitherWayAsync(userId, cancellationToken)).ToHashSet();
        var authors = (await follows.GetFollowingIdsAsync(userId, cancellationToken))
            .Where(id => !blocked.Contains(id))
            .Append(userId)
            .ToList();
        var (items, total) = await posts.SearchAsync(
            new PostSearch(null, null, null, IncludeHidden: false, AuthorIds: authors),
            request.Page,
            request.PageSize,
            cancellationToken);
        return new PagedResult<PostDto>(await views.BuildAsync(items, userId, cancellationToken), request.Page, request.PageSize, total);
    }
}

/// <summary>
/// The For you timeline (anyone): public posts from the last <c>Reels:RankingWindow</c> (30 days), ranked by reactions
/// and comments fading with age, with posts near the signed-in user's home area ranked higher (3 times within its
/// radius, 1.5 times within four times it); then older posts, newest first. People blocked either way are left out.
/// </summary>
public sealed record ListForYouPostsQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListForYouPostsQueryValidator : AbstractValidator<ListForYouPostsQuery>
{
    public ListForYouPostsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListForYouPostsQueryHandler(
    IPostRepository posts,
    IBlockDirectory blocks,
    IHomeAreas homeAreas,
    PostViews views,
    ICurrentUser caller,
    IOptions<ReelsOptions> options,
    TimeProvider clock)
    : IRequestHandler<ListForYouPostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListForYouPostsQuery request, CancellationToken cancellationToken)
    {
        var viewerId = caller.UserId();
        ForYouHome? home = null;
        IReadOnlyCollection<Guid>? blocked = null;
        if (viewerId is { } id)
        {
            blocked = await blocks.GetBlockedEitherWayAsync(id, cancellationToken);
            if (await homeAreas.GetAsync(id, cancellationToken) is { } area)
            {
                home = new ForYouHome(
                    GeoBox.Around(area.Latitude, area.Longitude, area.RadiusKm),
                    GeoBox.Around(area.Latitude, area.Longitude, Math.Min(area.RadiusKm * 4, 200)));
            }
        }

        var since = clock.GetUtcNow() - options.Value.RankingWindow;
        var (items, total) = await posts.ForYouAsync(new ForYouSearch(since, home, blocked), request.Page, request.PageSize, cancellationToken);
        return new PagedResult<PostDto>(await views.BuildAsync(items, viewerId, cancellationToken), request.Page, request.PageSize, total);
    }
}

/// <summary>A custom timeline: its name, types, optional place (name and radius only) and position.</summary>
public sealed record TimelineDto(
    Guid Id,
    string Name,
    IReadOnlyList<IncidentCategory> Types,
    string? PlaceName,
    decimal? Latitude,
    decimal? Longitude,
    int? RadiusKm,
    int Position)
{
    internal static TimelineDto From(Timeline t) =>
        new(t.Id, t.Name, t.Types, t.PlaceName, t.Latitude, t.Longitude, t.RadiusKm, t.Position);
}

/// <summary>The signed-in user's custom timelines, in order, and how many their plan allows.</summary>
public sealed record TimelinesDto(IReadOnlyList<TimelineDto> Timelines, int Limit, SubscriptionPlan Plan);

/// <summary>What a custom timeline shows: a name, one or more types, and optionally a place and radius.</summary>
public interface ITimelineRequest
{
    string Name { get; }

    IReadOnlyList<IncidentCategory> Types { get; }

    string? PlaceName { get; }

    decimal? Latitude { get; }

    decimal? Longitude { get; }

    int? RadiusKm { get; }
}

public abstract class TimelineRequestValidator<T> : AbstractValidator<T>
    where T : ITimelineRequest
{
    protected TimelineRequestValidator()
    {
        RuleFor(t => t.Name)
            .NotEmpty()
            .WithMessage("Give the timeline a name.")
            .Must(n => n is null || n.Trim().Length <= Timeline.NameMaxLength)
            .WithMessage($"Names are {Timeline.NameMaxLength} characters or fewer.")
            .NoPersonalDetails();
        RuleFor(t => t.Types)
            .NotEmpty()
            .WithMessage("Choose at least one type of bad driving.");
        RuleForEach(t => t.Types).IsInEnum();
        RuleFor(t => t)
            .Must(t => (t.Latitude is null) == (t.Longitude is null) && (t.Latitude is null) == (t.RadiusKm is null)
                && (t.Latitude is null || !string.IsNullOrWhiteSpace(t.PlaceName)))
            .OverridePropertyName("Place")
            .WithMessage("To limit a timeline to a place, choose the place and a radius.");
        RuleFor(t => t.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(t => t.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(t => t.RadiusKm)
            .Must(r => r is null || Timeline.Radiuses.Contains(r.Value))
            .WithMessage($"Choose a radius of {string.Join(", ", Timeline.Radiuses)} km.");
        RuleFor(t => t.PlaceName)
            .Must(p => p is null || p.Trim().Length <= Timeline.PlaceNameMaxLength)
            .WithMessage($"Place names are {Timeline.PlaceNameMaxLength} characters or fewer.");
    }
}

internal static class TimelineRequests
{
    public static TimelineDetails ToDetails(this ITimelineRequest r) => new(
        r.Name,
        r.Types.Distinct().ToList(),
        r is { Latitude: { } lat, Longitude: { } lng, RadiusKm: { } radius } ? new TimelineArea(r.PlaceName!, lat, lng, radius) : null);
}

/// <summary>Your custom timelines, in order, with your plan's limit (Free 1, Premium 5, Premium + 10).</summary>
public sealed record ListTimelinesQuery : IRequest<TimelinesDto>;

public sealed class ListTimelinesQueryHandler(ITimelineRepository timelines, IPlanResolver plans, ICurrentUser caller)
    : IRequestHandler<ListTimelinesQuery, TimelinesDto>
{
    public async Task<TimelinesDto> Handle(ListTimelinesQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        var list = await timelines.ListAsync(userId, cancellationToken);
        return new TimelinesDto(list.Select(TimelineDto.From).ToList(), Timeline.LimitFor(plan), plan);
    }
}

/// <summary>Adds a custom timeline at the end, if your plan allows another.</summary>
public sealed record CreateTimelineCommand(
    string Name,
    IReadOnlyList<IncidentCategory> Types,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null) : IRequest<TimelineDto>, ITimelineRequest;

public sealed class CreateTimelineCommandValidator : TimelineRequestValidator<CreateTimelineCommand>
{
}

public sealed class CreateTimelineCommandHandler(
    ITimelineRepository timelines,
    IPlanResolver plans,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<CreateTimelineCommand, TimelineDto>
{
    public async Task<TimelineDto> Handle(CreateTimelineCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        var existing = await timelines.ListAsync(userId, cancellationToken);
        var limit = Timeline.LimitFor(plan);
        if (existing.Count >= limit)
        {
            throw PostAccess.Invalid(
                "Timelines",
                plan == SubscriptionPlan.PremiumPlus
                    ? $"You can have up to {limit} custom timelines. Delete one first."
                    : $"Your plan allows {limit} custom {(limit == 1 ? "timeline" : "timelines")}. Delete one, or upgrade for more.");
        }

        var position = existing.Count == 0 ? 0 : existing.Max(t => t.Position) + 1;
        var timeline = Timeline.Create(userId, request.ToDetails(), position, clock.GetUtcNow());
        timelines.Add(timeline);
        await timelines.SaveChangesAsync(cancellationToken);
        return TimelineDto.From(timeline);
    }
}

/// <summary>Changes one of your custom timelines.</summary>
public sealed record UpdateTimelineCommand(
    Guid Id,
    string Name,
    IReadOnlyList<IncidentCategory> Types,
    string? PlaceName = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? RadiusKm = null) : IRequest<TimelineDto>, ITimelineRequest;

public sealed class UpdateTimelineCommandValidator : TimelineRequestValidator<UpdateTimelineCommand>
{
}

public sealed class UpdateTimelineCommandHandler(ITimelineRepository timelines, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<UpdateTimelineCommand, TimelineDto>
{
    public async Task<TimelineDto> Handle(UpdateTimelineCommand request, CancellationToken cancellationToken)
    {
        var timeline = await timelines.GetAsync(caller.RequireUserId(), request.Id, cancellationToken)
            ?? throw new NotFoundException("Timeline", request.Id);
        timeline.Update(request.ToDetails(), clock.GetUtcNow());
        await timelines.SaveChangesAsync(cancellationToken);
        return TimelineDto.From(timeline);
    }
}

/// <summary>Deletes one of your custom timelines. Does nothing if it's already gone.</summary>
public sealed record DeleteTimelineCommand(Guid Id) : IRequest;

public sealed class DeleteTimelineCommandHandler(ITimelineRepository timelines, ICurrentUser caller)
    : IRequestHandler<DeleteTimelineCommand>
{
    public async Task Handle(DeleteTimelineCommand request, CancellationToken cancellationToken)
    {
        var timeline = await timelines.GetAsync(caller.RequireUserId(), request.Id, cancellationToken);
        if (timeline is null)
        {
            return;
        }

        timelines.Remove(timeline);
        await timelines.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Puts your custom timelines in this order. Every one of them must be listed once.</summary>
public sealed record ReorderTimelinesCommand(IReadOnlyList<Guid> Ids) : IRequest<TimelinesDto>;

public sealed class ReorderTimelinesCommandHandler(ITimelineRepository timelines, IPlanResolver plans, ICurrentUser caller)
    : IRequestHandler<ReorderTimelinesCommand, TimelinesDto>
{
    public async Task<TimelinesDto> Handle(ReorderTimelinesCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var list = await timelines.ListAsync(userId, cancellationToken);
        var ids = request.Ids ?? [];
        if (ids.Count != list.Count || ids.Distinct().Count() != ids.Count || !list.All(t => ids.Contains(t.Id)))
        {
            throw PostAccess.Invalid(nameof(request.Ids), "List each of your timelines once, in the new order.");
        }

        var byId = list.ToDictionary(t => t.Id);
        for (var i = 0; i < ids.Count; i++)
        {
            byId[ids[i]].MoveTo(i);
        }

        await timelines.SaveChangesAsync(cancellationToken);
        var plan = plans.CurrentPlan() ?? SubscriptionPlan.Free;
        return new TimelinesDto(ids.Select(id => TimelineDto.From(byId[id])).ToList(), Timeline.LimitFor(plan), plan);
    }
}

/// <summary>A custom timeline's posts, newest first: any of its types, within its place's radius if it has one.</summary>
public sealed record ListTimelinePostsQuery(Guid Id, int Page = 1, int PageSize = 20) : IRequest<PagedResult<PostDto>>;

public sealed class ListTimelinePostsQueryValidator : AbstractValidator<ListTimelinePostsQuery>
{
    public ListTimelinePostsQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 50);
    }
}

public sealed class ListTimelinePostsQueryHandler(
    ITimelineRepository timelines,
    IPostRepository posts,
    IBlockDirectory blocks,
    PostViews views,
    ICurrentUser caller)
    : IRequestHandler<ListTimelinePostsQuery, PagedResult<PostDto>>
{
    public async Task<PagedResult<PostDto>> Handle(ListTimelinePostsQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var timeline = await timelines.GetAsync(userId, request.Id, cancellationToken)
            ?? throw new NotFoundException("Timeline", request.Id);
        var area = timeline is { Latitude: { } lat, Longitude: { } lng, RadiusKm: { } radius } ? GeoBox.Around(lat, lng, radius) : null;
        var blocked = await blocks.GetBlockedIdsAsync(userId, cancellationToken);
        var (items, total) = await posts.SearchAsync(
            new PostSearch(null, null, area, IncludeHidden: false, ExcludeAuthorIds: blocked, TypeFlags: timeline.TypeFlags),
            request.Page,
            request.PageSize,
            cancellationToken);
        return new PagedResult<PostDto>(await views.BuildAsync(items, userId, cancellationToken), request.Page, request.PageSize, total);
    }
}
