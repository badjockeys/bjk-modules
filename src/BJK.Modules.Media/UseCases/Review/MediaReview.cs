using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace BJK.Modules.Media.UseCases.Review;

/// <summary>
/// Media as a moderator sees it in the review queue, with links to the blurred files. While it's waiting for review
/// the links are signed links to the private copies, valid for <see cref="MediaReview.PreviewLifetime"/>; once Ready
/// they're the public addresses. Never the unblurred original (see <see cref="GetOriginalForReviewQuery"/>).
/// </summary>
/// <param name="Id">Media id.</param>
/// <param name="UserId">Uploader.</param>
/// <param name="MediaType">Video or Image.</param>
/// <param name="Status">Current state.</param>
/// <param name="ReviewReason">Why it needs review.</param>
/// <param name="FacesBlurred">Faces blurred automatically.</param>
/// <param name="PlatesBlurred">Number plates blurred automatically.</param>
/// <param name="Revision">Times a moderator has had it re-processed.</param>
/// <param name="ManualRedactions">Blur regions moderators added.</param>
/// <param name="DurationSeconds">Video length.</param>
/// <param name="Width">Width of the original as seen.</param>
/// <param name="Height">Height of the original as seen.</param>
/// <param name="CreatedAt">Upload started.</param>
/// <param name="ProcessedAt">Last processed.</param>
/// <param name="PreviewUrl">The blurred video or photo.</param>
/// <param name="PreviewThumbnailUrl">Its thumbnail.</param>
/// <param name="PreviewExpiresAt">When signed preview links stop working; null for public links.</param>
public sealed record MediaReviewDto(
    Guid Id,
    Guid UserId,
    MediaType MediaType,
    MediaStatus Status,
    string? ReviewReason,
    int? FacesBlurred,
    int? PlatesBlurred,
    int Revision,
    IReadOnlyList<ManualRedaction> ManualRedactions,
    decimal? DurationSeconds,
    int? Width,
    int? Height,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt,
    Uri? PreviewUrl,
    Uri? PreviewThumbnailUrl,
    DateTimeOffset? PreviewExpiresAt);

/// <summary>A short-lived link to the unblurred original.</summary>
/// <param name="Url">Signed download link.</param>
/// <param name="ExpiresAt">When it stops working.</param>
public sealed record OriginalDownload(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Shared pieces of the review use cases.</summary>
public static class MediaReview
{
    /// <summary>How long signed preview and original links work.</summary>
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    internal static async Task<MediaReviewDto> ToDtoAsync(
        MediaItem item,
        IMediaStorage storage,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        Uri? preview = null;
        Uri? thumbnail = null;
        DateTimeOffset? expires = null;
        if (item.Status == MediaStatus.NeedsReview)
        {
            preview = await storage.CreateOriginalDownloadUrlAsync(
                MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision), PreviewLifetime, cancellationToken);
            thumbnail = await storage.CreateOriginalDownloadUrlAsync(
                MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision), PreviewLifetime, cancellationToken);
            expires = clock.GetUtcNow() + PreviewLifetime;
        }
        else if (item.Status == MediaStatus.Ready)
        {
            preview = item.PlaybackKey is null ? null : storage.PublicUrl(item.PlaybackKey);
            thumbnail = item.ThumbnailKey is null ? null : storage.PublicUrl(item.ThumbnailKey);
        }

        return new MediaReviewDto(
            item.Id,
            item.UserId,
            item.MediaType,
            item.Status,
            item.ReviewReason,
            item.FacesBlurred,
            item.PlatesBlurred,
            item.Revision,
            item.ManualRedactions,
            item.DurationSeconds,
            item.Width,
            item.Height,
            item.CreatedAt,
            item.ProcessedAt,
            preview,
            thumbnail,
            expires);
    }

    internal static async Task<MediaItem> GetAsync(IMediaRepository media, Guid mediaId, CancellationToken cancellationToken) =>
        await media.GetByIdAsync(mediaId, cancellationToken) ?? throw new NotFoundException("Media", mediaId);

    internal static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);

    internal static void EnsureNeedsReview(MediaItem item)
    {
        if (item.Status != MediaStatus.NeedsReview)
        {
            throw Invalid(nameof(MediaItem.Status), $"This media is {item.Status}, not waiting for review.");
        }
    }
}

/// <summary>The review queue: media waiting for a moderator, oldest first.</summary>
/// <param name="Page">Page number, from 1.</param>
/// <param name="PageSize">Items per page, 1 to 100.</param>
public sealed record ListMediaForReviewQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<MediaReviewDto>>;

public sealed class ListMediaForReviewQueryValidator : AbstractValidator<ListMediaForReviewQuery>
{
    public ListMediaForReviewQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListMediaForReviewQueryHandler(IMediaRepository media, IMediaStorage storage, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<ListMediaForReviewQuery, PagedResult<MediaReviewDto>>
{
    public async Task<PagedResult<MediaReviewDto>> Handle(ListMediaForReviewQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var (items, total) = await media.ListByStatusAsync(MediaStatus.NeedsReview, request.Page, request.PageSize, cancellationToken);
        var dtos = new List<MediaReviewDto>(items.Count);
        foreach (var item in items)
        {
            dtos.Add(await MediaReview.ToDtoAsync(item, storage, clock, cancellationToken));
        }

        return new PagedResult<MediaReviewDto>(dtos, request.Page, request.PageSize, total);
    }
}

/// <summary>One media item with links to its blurred files, for a moderator.</summary>
/// <param name="MediaId">The media.</param>
public sealed record GetMediaForReviewQuery(Guid MediaId) : IRequest<MediaReviewDto>;

public sealed class GetMediaForReviewQueryHandler(IMediaRepository media, IMediaStorage storage, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<GetMediaForReviewQuery, MediaReviewDto>
{
    public async Task<MediaReviewDto> Handle(GetMediaForReviewQuery request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        return await MediaReview.ToDtoAsync(item, storage, clock, cancellationToken);
    }
}

/// <summary>
/// A short-lived link to the unblurred original. Admins only, and every use is recorded in the moderation audit log,
/// since it shows faces and plates in the clear. Originals are deleted 90 days after upload.
/// </summary>
/// <param name="MediaId">The media.</param>
public sealed record GetOriginalForReviewQuery(Guid MediaId) : IRequest<OriginalDownload>;

public sealed class GetOriginalForReviewQueryHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<GetOriginalForReviewQuery, OriginalDownload>
{
    public async Task<OriginalDownload> Handle(GetOriginalForReviewQuery request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        if (item.OriginalDeletesAt is { } deletesAt && deletesAt <= clock.GetUtcNow())
        {
            throw new NotFoundException("Original", request.MediaId);
        }

        var url = await storage.CreateOriginalDownloadUrlAsync(item.OriginalKey, MediaReview.PreviewLifetime, cancellationToken);

        await audit.RecordAsync(
            new ModerationAuditEntry("Media", ModerationActions.ViewMediaOriginal, "Media", item.Id.ToString(), "Opened the unblurred original."),
            cancellationToken);
        return new OriginalDownload(url, clock.GetUtcNow() + MediaReview.PreviewLifetime);
    }
}

/// <summary>A moderator approves the blurred files: they're published and the media becomes Ready, so it can be posted.</summary>
/// <param name="MediaId">The media.</param>
public sealed record ApproveMediaCommand(Guid MediaId) : IRequest<MediaReviewDto>;

public sealed class ApproveMediaCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<ApproveMediaCommand, MediaReviewDto>
{
    public async Task<MediaReviewDto> Handle(ApproveMediaCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        MediaReview.EnsureNeedsReview(item);

        var (playbackType, thumbnailType) = item.MediaType == MediaType.Video
            ? ("video/mp4", "image/jpeg")
            : ("image/webp", "image/webp");

        // Publish first: if copying fails the media stays in review and approving can be retried.
        await storage.PublishPrivateAsync(
            MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision),
            MediaKeys.Playback(item.UserId, item.Id, item.MediaType, item.Revision),
            playbackType,
            cancellationToken);
        await storage.PublishPrivateAsync(
            MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision),
            MediaKeys.Thumbnail(item.UserId, item.Id, item.MediaType, item.Revision),
            thumbnailType,
            cancellationToken);

        var reviewFiles = (Playback: MediaKeys.ReviewPlayback(item.UserId, item.Id, item.MediaType, item.Revision),
            Thumbnail: MediaKeys.ReviewThumbnail(item.UserId, item.Id, item.MediaType, item.Revision));
        item.Approve(caller.UserId(), clock.GetUtcNow());
        await media.SaveChangesAsync(cancellationToken);
        await storage.DeleteOriginalAsync(reviewFiles.Playback, cancellationToken);
        await storage.DeleteOriginalAsync(reviewFiles.Thumbnail, cancellationToken);

        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Media",
                ModerationActions.ApproveMedia,
                "Media",
                item.Id.ToString(),
                $"Approved the blurred files (revision {item.Revision}).",
                $"Status={MediaStatus.NeedsReview}",
                $"Status={item.Status}"),
            cancellationToken);
        return await MediaReview.ToDtoAsync(item, storage, clock, cancellationToken);
    }
}

/// <summary>A moderator rejects media waiting for review: it becomes Failed with the reason, which the uploader sees.</summary>
/// <param name="MediaId">The media.</param>
/// <param name="Reason">Shown to the uploader, e.g. "A face couldn't be blurred reliably."</param>
public sealed record RejectMediaCommand(Guid MediaId, string Reason) : IRequest<MediaReviewDto>;

public sealed class RejectMediaCommandValidator : AbstractValidator<RejectMediaCommand>
{
    public RejectMediaCommandValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(MediaItem.FailureReasonMaxLength);
    }
}

public sealed class RejectMediaCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<RejectMediaCommand, MediaReviewDto>
{
    public async Task<MediaReviewDto> Handle(RejectMediaCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        MediaReview.EnsureNeedsReview(item);

        await MediaFiles.DeleteReviewFilesAsync(storage, item, cancellationToken);
        item.Reject(request.Reason.Trim(), caller.UserId(), clock.GetUtcNow());
        await media.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Media",
                ModerationActions.RejectMedia,
                "Media",
                item.Id.ToString(),
                request.Reason.Trim(),
                $"Status={MediaStatus.NeedsReview}",
                $"Status={item.Status}"),
            cancellationToken);
        return await MediaReview.ToDtoAsync(item, storage, clock, cancellationToken);
    }
}

/// <summary>
/// A moderator adds blur regions for things automatic detection missed, and the media is processed again (automatic
/// detection plus every manual region). The result always comes back to the review queue. Works on media waiting for
/// review or already Ready (e.g. after a report); a Ready item's public files are removed straight away.
/// </summary>
/// <param name="MediaId">The media.</param>
/// <param name="Regions">Regions to add, as fractions of the picture, with optional time ranges for videos.</param>
public sealed record AddManualRedactionsCommand(Guid MediaId, IReadOnlyList<ManualRedaction> Regions) : IRequest<MediaReviewDto>;

public sealed class AddManualRedactionsCommandValidator : AbstractValidator<AddManualRedactionsCommand>
{
    private const double Tolerance = 0.0001;

    public AddManualRedactionsCommandValidator()
    {
        RuleFor(c => c.Regions).NotEmpty().Must(r => r is null || r.Count <= ManualRedaction.MaxPerMedia)
            .WithMessage($"Add at most {ManualRedaction.MaxPerMedia} regions.");
        RuleForEach(c => c.Regions).ChildRules(region =>
        {
            region.RuleFor(r => r.X).InclusiveBetween(0, 1);
            region.RuleFor(r => r.Y).InclusiveBetween(0, 1);
            region.RuleFor(r => r.Width).GreaterThan(0).LessThanOrEqualTo(1)
                .Must((r, width) => r.X + width <= 1 + Tolerance).WithMessage("The region must fit inside the picture.");
            region.RuleFor(r => r.Height).GreaterThan(0).LessThanOrEqualTo(1)
                .Must((r, height) => r.Y + height <= 1 + Tolerance).WithMessage("The region must fit inside the picture.");
            region.RuleFor(r => r.StartSeconds).GreaterThanOrEqualTo(0).When(r => r.StartSeconds is not null);
            region.RuleFor(r => r.EndSeconds).GreaterThanOrEqualTo(r => r.StartSeconds ?? 0).When(r => r.EndSeconds is not null);
        });
    }
}

public sealed class AddManualRedactionsCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IMediaJobQueue queue,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AddManualRedactionsCommand, MediaReviewDto>
{
    public async Task<MediaReviewDto> Handle(AddManualRedactionsCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        if (item.Status is not (MediaStatus.NeedsReview or MediaStatus.Ready))
        {
            throw MediaReview.Invalid(nameof(MediaItem.Status), $"This media is {item.Status}; blur regions can be added once it has been processed.");
        }

        if (item.ManualRedactions.Count + request.Regions.Count > ManualRedaction.MaxPerMedia)
        {
            throw MediaReview.Invalid(nameof(request.Regions), $"Media can have at most {ManualRedaction.MaxPerMedia} blur regions.");
        }

        if (item.OriginalDeletesAt is { } deletesAt && deletesAt <= clock.GetUtcNow())
        {
            throw MediaReview.Invalid(nameof(request.MediaId), "The original has been deleted (after 90 days), so it can't be processed again.");
        }

        // Take down what's there now: public files of Ready media (something unblurred may be showing), or the
        // review copies. Cloudflare may keep serving a cached public file until it's purged.
        if (item.Status == MediaStatus.Ready)
        {
            var published = new[] { item.PlaybackKey, item.ThumbnailKey }.OfType<string>().ToList();
            if (published.Count > 0)
            {
                await storage.DeletePublicAsync(published, cancellationToken);
            }
        }
        else
        {
            await MediaFiles.DeleteReviewFilesAsync(storage, item, cancellationToken);
        }

        var before = item.Status;
        item.AddManualRedactions(request.Regions, clock.GetUtcNow());

        // Save first, then queue: the worker skips media that isn't Processing yet, so queueing first could lose the job.
        await media.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(
            new MediaProcessingJob(item.Id, item.UserId, item.MediaType, item.OriginalKey, item.OriginalContentType),
            cancellationToken);

        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Media",
                ModerationActions.AddMediaRedactions,
                "Media",
                item.Id.ToString(),
                $"Added {request.Regions.Count} blur region(s) and processed it again (revision {item.Revision}).",
                $"Status={before}",
                $"Status={item.Status}"),
            cancellationToken);
        return await MediaReview.ToDtoAsync(item, storage, clock, cancellationToken);
    }
}
