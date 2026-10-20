using System.Text.Json.Serialization;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Posts.UseCases.Drafts;

/// <summary>A saved draft, to reopen in the composer.</summary>
/// <param name="Id">The draft's id.</param>
/// <param name="Title">The title so far.</param>
/// <param name="Description">"What happened?" so far.</param>
/// <param name="Types">Types of bad driving chosen, the main one first.</param>
/// <param name="IncidentAt">When it happened.</param>
/// <param name="Latitude">Where it happened.</param>
/// <param name="Longitude">Where it happened.</param>
/// <param name="PlaceName">The road and town.</param>
/// <param name="IsSensitive">Marked as graphic.</param>
/// <param name="Passengers">Passengers' @handles, as typed (checked when the post is published).</param>
/// <param name="VehicleType">The vehicle driven badly.</param>
/// <param name="VehicleMake">Its make.</param>
/// <param name="VehicleModel">Its model.</param>
/// <param name="VehicleColour">Its colour.</param>
/// <param name="ReportedToPolice">Whether it was reported to the police.</param>
/// <param name="Media">The photos or video, in order. Ones deleted since are left out.</param>
/// <param name="CreatedAt">When the draft was first saved.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
/// <param name="ExpiresAt">When it's deleted unless saved again.</param>
public sealed record DraftDto(
    Guid Id,
    string? Title,
    string? Description,
    IReadOnlyList<IncidentCategory> Types,
    DateTimeOffset? IncidentAt,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceName,
    bool IsSensitive,
    IReadOnlyList<string> Passengers,
    VehicleType? VehicleType,
    string? VehicleMake,
    string? VehicleModel,
    VehicleColour? VehicleColour,
    bool? ReportedToPolice,
    IReadOnlyList<DraftMediaDto> Media,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A photo or video in a draft.</summary>
/// <param name="MediaId">The upload's id.</param>
/// <param name="MediaType">Photo or video.</param>
/// <param name="Status">Ready, still Processing, and so on.</param>
/// <param name="ThumbnailUrl">A small picture of it, once processed.</param>
/// <param name="PlaybackUrl">The photo or video itself, once processed.</param>
/// <param name="DurationSeconds">Videos: how long it is.</param>
/// <param name="Width">Width in pixels, once processed.</param>
/// <param name="Height">Height in pixels, once processed.</param>
/// <param name="FailureReason">Why processing failed, if it did.</param>
public sealed record DraftMediaDto(
    Guid MediaId,
    MediaType MediaType,
    MediaStatus Status,
    Uri? ThumbnailUrl,
    Uri? PlaybackUrl,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    string? FailureReason);

/// <summary>
/// Saves a draft: a new one when <c>DraftId</c> is null (up to <see cref="PostDraft.MaxPerAuthor"/> per author), or
/// replaces what's in an existing one. Everything is optional, but a draft can't be empty. The photos and videos must
/// be the caller's own uploads and not already in a post. Saving restarts the 30 days before it expires.
/// </summary>
public sealed record SaveDraftCommand(
    string? Title,
    string? Description,
    IReadOnlyList<IncidentCategory>? Types,
    DateTimeOffset? IncidentAt,
    decimal? Latitude,
    decimal? Longitude,
    string? PlaceName,
    bool IsSensitive,
    IReadOnlyList<string>? Passengers,
    VehicleType? VehicleType,
    string? VehicleMake,
    string? VehicleModel,
    VehicleColour? VehicleColour,
    bool? ReportedToPolice,
    IReadOnlyList<Guid>? MediaIds,
    [property: JsonIgnore] Guid? DraftId = null) : IRequest<DraftDto>
{
    internal DraftContent ToContent() => new(
        Title,
        Description,
        Types ?? [],
        IncidentAt,
        Latitude,
        Longitude,
        PlaceName,
        IsSensitive,
        Passengers ?? [],
        VehicleType,
        VehicleMake,
        VehicleModel,
        VehicleColour,
        ReportedToPolice,
        MediaIds ?? []);

    internal bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Description)
        && Types is not { Count: > 0 }
        && MediaIds is not { Count: > 0 }
        && string.IsNullOrWhiteSpace(PlaceName)
        && Latitude is null
        && IncidentAt is null
        && Passengers is not { Count: > 0 }
        && VehicleType is null;
}

public sealed class SaveDraftCommandValidator : AbstractValidator<SaveDraftCommand>
{
    /// <summary>The message when there's nothing to save.</summary>
    public const string EmptyMessage = "There's nothing to save yet.";

    public SaveDraftCommandValidator()
    {
        RuleFor(c => c).Must(c => !c.IsEmpty).OverridePropertyName("Draft").WithMessage(EmptyMessage);
        RuleFor(c => c.Title).MaximumLength(Post.TitleMaxLength);
        RuleFor(c => c.Description).MaximumLength(Post.DescriptionInputMaxLength);
        RuleFor(c => c.PlaceName).MaximumLength(Post.PlaceNameMaxLength);
        RuleFor(c => c.VehicleMake).MaximumLength(VehicleCatalogue.NameMaxLength);
        RuleFor(c => c.VehicleModel).MaximumLength(VehicleCatalogue.NameMaxLength);
        RuleFor(c => c.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(c => c.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(c => c.Types)
            .Must(t => t is null || t.Count <= Enum.GetValues<IncidentCategory>().Length)
            .WithMessage("Too many types of bad driving.");
        RuleForEach(c => c.Types).IsInEnum();
        RuleFor(c => c.Passengers)
            .Must(p => p is null || p.Count <= PostPassenger.MaxPerPost)
            .WithMessage($"A post can have up to {PostPassenger.MaxPerPost} passengers.")
            .Must(p => p is null || string.Join(',', p.Select(h => h.Trim().TrimStart('@'))).Length <= PostDraft.PassengersMaxLength)
            .WithMessage("Those usernames are too long.");
        RuleFor(c => c.MediaIds)
            .Must(ids => ids is null || ids.Count <= Post.MaxMedia)
            .WithMessage($"A post can have up to {Post.MaxMedia} photos or videos.")
            .Must(ids => ids is null || !ids.Contains(Guid.Empty))
            .WithMessage("Media ids can't be empty.");
    }
}

public sealed class SaveDraftCommandHandler(
    IDraftRepository drafts,
    IPostRepository posts,
    IMediaDirectory media,
    DraftViews views,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<SaveDraftCommand, DraftDto>
{
    /// <summary>The message when the author already has the most drafts allowed.</summary>
    public static readonly string LimitMessage =
        $"You can keep up to {PostDraft.MaxPerAuthor} drafts. Post or delete one first.";

    public async Task<DraftDto> Handle(SaveDraftCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var mediaIds = (request.MediaIds ?? []).Distinct().ToList();
        if (mediaIds.Count > 0)
        {
            var found = (await media.GetAsync(mediaIds, cancellationToken)).ToDictionary(m => m.Id);
            if (mediaIds.Any(id => !found.TryGetValue(id, out var item) || item.UserId != userId || item.Status == MediaStatus.Deleted))
            {
                throw PostAccess.Invalid(nameof(request.MediaIds), "A photo or video isn't one of your uploads.");
            }

            if (await posts.AnyUseMediaAsync(mediaIds, cancellationToken))
            {
                throw PostAccess.Invalid(nameof(request.MediaIds), "A photo or video is already in a post.");
            }
        }

        var now = clock.GetUtcNow();
        PostDraft draft;
        if (request.DraftId is { } draftId)
        {
            draft = await GetOwnAsync(drafts, caller, draftId, cancellationToken);
            draft.Update(request.ToContent() with { MediaIds = mediaIds }, now);
        }
        else
        {
            if (await drafts.CountForAuthorAsync(userId, cancellationToken) >= PostDraft.MaxPerAuthor)
            {
                throw PostAccess.Invalid("Drafts", LimitMessage);
            }

            draft = PostDraft.Create(userId, request.ToContent() with { MediaIds = mediaIds }, now);
            drafts.Add(draft);
        }

        await drafts.SaveChangesAsync(cancellationToken);
        return (await views.BuildAsync([draft], cancellationToken))[0];
    }

    /// <summary>The caller's own draft. Someone else's is reported as not found.</summary>
    internal static async Task<PostDraft> GetOwnAsync(
        IDraftRepository drafts,
        ICurrentUser caller,
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var draft = await drafts.GetAsync(draftId, cancellationToken);
        return draft is not null && draft.AuthorId == userId ? draft : throw new NotFoundException("Draft", draftId);
    }
}

/// <summary>The signed-in user's drafts, last saved first.</summary>
public sealed record ListDraftsQuery : IRequest<IReadOnlyList<DraftDto>>;

public sealed class ListDraftsQueryHandler(IDraftRepository drafts, DraftViews views, ICurrentUser caller)
    : IRequestHandler<ListDraftsQuery, IReadOnlyList<DraftDto>>
{
    public async Task<IReadOnlyList<DraftDto>> Handle(ListDraftsQuery request, CancellationToken cancellationToken) =>
        await views.BuildAsync(await drafts.ListForAuthorAsync(caller.RequireUserId(), cancellationToken), cancellationToken);
}

/// <summary>One of the signed-in user's drafts.</summary>
public sealed record GetDraftQuery(Guid DraftId) : IRequest<DraftDto>;

public sealed class GetDraftQueryHandler(IDraftRepository drafts, DraftViews views, ICurrentUser caller)
    : IRequestHandler<GetDraftQuery, DraftDto>
{
    public async Task<DraftDto> Handle(GetDraftQuery request, CancellationToken cancellationToken)
    {
        var draft = await SaveDraftCommandHandler.GetOwnAsync(drafts, caller, request.DraftId, cancellationToken);
        return (await views.BuildAsync([draft], cancellationToken))[0];
    }
}

/// <summary>Deletes one of the signed-in user's drafts, and its photos and videos (unless they're in a post or another draft).</summary>
public sealed record DeleteDraftCommand(Guid DraftId) : IRequest;

public sealed class DeleteDraftCommandHandler(DraftDeletion deletion, IDraftRepository drafts, ICurrentUser caller)
    : IRequestHandler<DeleteDraftCommand>
{
    public async Task Handle(DeleteDraftCommand request, CancellationToken cancellationToken)
    {
        var draft = await SaveDraftCommandHandler.GetOwnAsync(drafts, caller, request.DraftId, cancellationToken);
        await deletion.DeleteAsync([draft], cancellationToken);
    }
}

/// <summary>Deletes drafts and the media only they were using. Used when the author deletes one, and when they expire.</summary>
public sealed class DraftDeletion(
    IDraftRepository drafts,
    IPostRepository posts,
    IMediaDirectory media,
    ILogger<DraftDeletion> logger)
{
    /// <summary>Removes the drafts, then (best effort) the photos and videos nothing else uses.</summary>
    /// <param name="items">Tracked drafts to delete.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    public async Task DeleteAsync(IReadOnlyList<PostDraft> items, CancellationToken cancellationToken)
    {
        var deleting = items.Select(d => d.Id).ToList();
        var ids = items.SelectMany(d => d.Media.Select(m => m.MediaId)).Distinct().ToList();
        var inOtherDrafts = await drafts.MediaInOtherDraftsAsync(ids, deleting, cancellationToken);
        var orphans = new List<Guid>();
        foreach (var id in ids.Where(id => !inOtherDrafts.Contains(id)))
        {
            if (!await posts.AnyUseMediaAsync([id], cancellationToken))
            {
                orphans.Add(id);
            }
        }

        foreach (var draft in items)
        {
            drafts.Remove(draft);
        }

        await drafts.SaveChangesAsync(cancellationToken);
        if (orphans.Count == 0)
        {
            return;
        }

        try
        {
            await media.DeleteAsync(orphans, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Drafts were deleted but their media {MediaIds} could not be removed.", orphans);
        }
    }
}

/// <summary>Turns drafts into <see cref="DraftDto"/>s, with their media's current state and links.</summary>
public sealed class DraftViews(IMediaDirectory media)
{
    public async Task<IReadOnlyList<DraftDto>> BuildAsync(IReadOnlyList<PostDraft> items, CancellationToken cancellationToken)
    {
        var ids = items.SelectMany(d => d.Media.Select(m => m.MediaId)).Distinct().ToList();
        var found = ids.Count == 0
            ? new Dictionary<Guid, MediaSummary>()
            : (await media.GetWithSensitiveLinksAsync(ids, cancellationToken)).ToDictionary(m => m.Id);

        return items.Select(d => new DraftDto(
                d.Id,
                d.Title,
                d.Description,
                d.Types,
                d.IncidentAt,
                d.Latitude,
                d.Longitude,
                d.PlaceName,
                d.IsSensitive,
                d.Passengers,
                d.VehicleType,
                d.VehicleMake,
                d.VehicleModel,
                d.VehicleColour,
                d.ReportedToPolice,
                d.Media
                    .OrderBy(m => m.Position)
                    .Where(m => found.TryGetValue(m.MediaId, out var item) && item.Status != MediaStatus.Deleted)
                    .Select(m => found[m.MediaId])
                    .Select(m => new DraftMediaDto(
                        m.Id,
                        m.MediaType,
                        m.Status,
                        m.ThumbnailUrl,
                        m.PlaybackUrl,
                        m.DurationSeconds,
                        m.Width,
                        m.Height,
                        m.FailureReason))
                    .ToList(),
                d.CreatedAt,
                d.UpdatedAt,
                d.ExpiresAt))
            .ToList();
    }
}
