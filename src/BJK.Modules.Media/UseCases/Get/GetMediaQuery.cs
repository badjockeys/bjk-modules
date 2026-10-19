using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.UseCases.Get;

/// <summary>
/// One piece of media. Owners and admins see it in any state; everyone else only once it's Ready,
/// so half-processed uploads (still carrying GPS data in the original) are never exposed. Sensitive media is blurred
/// (preview only) for anyone but the owner, moderators and age-verified adults.
/// </summary>
/// <param name="MediaId">The media to get.</param>
/// <param name="AsModerator">True on the admin endpoint: a moderator sees media in any state.</param>
public sealed record GetMediaQuery(Guid MediaId, bool AsModerator = false) : IRequest<MediaDto>;

public sealed class GetMediaQueryHandler(
    IMediaRepository media,
    IMediaStorage storage,
    ICurrentUser caller,
    IOptions<MediaOptions> options,
    TimeProvider clock)
    : IRequestHandler<GetMediaQuery, MediaDto>
{
    public async Task<MediaDto> Handle(GetMediaQuery request, CancellationToken cancellationToken)
    {
        if (request.AsModerator)
        {
            caller.RequireModerator(MediaModule.ManageScope);
        }

        var item = await media.GetByIdAsync(request.MediaId, cancellationToken)
            ?? throw new NotFoundException("Media", request.MediaId);

        var isOwner = Guid.TryParse(caller.IdentityId, out var userId) && item.IsOwnedBy(userId);
        if (item.Status != MediaStatus.Ready && !isOwner && !request.AsModerator)
        {
            // Same response as missing, so other people can't probe for unpublished media.
            throw new NotFoundException("Media", request.MediaId);
        }

        var sensitive = item.IsRestricted || await media.IsSensitiveAsync(item.Id, cancellationToken);
        var mayView = isOwner
            || request.AsModerator
            || caller.HasModeratorRights(MediaModule.ManageScope)
            || (caller.UserId() is not null && caller.AgeVerified);
        return await MediaDto.ForViewerAsync(
            item, storage, sensitive, mayView, options.Value.SensitiveLinkLifetime, clock.GetUtcNow(), cancellationToken);
    }
}

/// <summary>The signed-in user's own uploads, newest first, in every state except deleted.</summary>
public sealed record ListMyMediaQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<MediaDto>>;

public sealed class ListMyMediaQueryValidator : AbstractValidator<ListMyMediaQuery>
{
    public ListMyMediaQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListMyMediaQueryHandler(
    IMediaRepository media,
    IMediaStorage storage,
    ICurrentUser caller,
    IOptions<MediaOptions> options,
    TimeProvider clock)
    : IRequestHandler<ListMyMediaQuery, PagedResult<MediaDto>>
{
    public async Task<PagedResult<MediaDto>> Handle(ListMyMediaQuery request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var (items, total) = await media.ListByUserAsync(userId, request.Page, request.PageSize, cancellationToken);
        var sensitive = await media.GetSensitiveIdsAsync(items.Select(m => m.Id).ToList(), cancellationToken);

        // The uploader always sees their own media, sensitive or not.
        var dtos = new List<MediaDto>(items.Count);
        foreach (var item in items)
        {
            dtos.Add(await MediaDto.ForViewerAsync(
                item, storage, sensitive.Contains(item.Id), mayView: true, options.Value.SensitiveLinkLifetime, clock.GetUtcNow(), cancellationToken));
        }

        return new PagedResult<MediaDto>(dtos, request.Page, request.PageSize, total);
    }
}
