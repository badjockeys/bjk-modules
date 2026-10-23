using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Media.UseCases.Share;

/// <summary>Where a video's share copy is, from its owner's point of view.</summary>
public enum ShareCopyState
{
    /// <summary>Not asked for, out of date (the video was re-processed) or expired: ask for one.</summary>
    None,

    /// <summary>Being made; ask again in a few seconds.</summary>
    Rendering,

    /// <summary>Made: <see cref="ShareCopyDto.DownloadUrl"/> downloads it.</summary>
    Ready,

    /// <summary>Couldn't be made; asking again tries afresh.</summary>
    Failed,
}

/// <summary>A video's vertical copy for TikTok, Reels, Shorts and X.</summary>
/// <param name="State">Where it is.</param>
/// <param name="DownloadUrl">When Ready: a short-lived link that downloads it as <c>bad-jockeys-clip.mp4</c>.</param>
/// <param name="LinkExpiresAt">When the link stops working (ask again for a fresh one).</param>
public sealed record ShareCopyDto(ShareCopyState State, Uri? DownloadUrl = null, DateTimeOffset? LinkExpiresAt = null);

/// <summary>
/// The owner's video's share copy (owner, 23 Oct 2026: only the person who posted a clip can download it). Anyone else
/// gets 404, as if it didn't exist.
/// </summary>
/// <param name="MediaId">The video.</param>
public sealed record GetShareCopyQuery(Guid MediaId) : IRequest<ShareCopyDto>;

/// <summary>
/// Asks for the owner's video's share copy: made by the media worker from the published file (already blurred and
/// silent), with a watermark and end card. If a current one exists, or one is being made, that's returned instead of
/// starting again. Only Ready videos that aren't marked sensitive can be shared.
/// </summary>
/// <param name="MediaId">The video.</param>
public sealed record RequestShareCopyCommand(Guid MediaId) : IRequest<ShareCopyDto>;

internal static class ShareCopies
{
    /// <summary>How long a download link works.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The name the copy downloads as.</summary>
    public const string FileName = "bad-jockeys-clip.mp4";

    /// <summary>The caller's own media, or 404 for anyone else's (so nobody can probe for other people's videos).</summary>
    public static async Task<MediaItem> GetOwnAsync(IMediaRepository media, ICurrentUser caller, Guid mediaId, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var item = await media.GetByIdAsync(mediaId, cancellationToken);
        return item is not null && item.IsOwnedBy(userId) ? item : throw new NotFoundException("Media", mediaId);
    }

    /// <summary>Why the video can't be shared now, or null if it can.</summary>
    public static async Task<string?> WhyNotAsync(IMediaRepository media, MediaItem item, CancellationToken cancellationToken)
    {
        if (item.MediaType != MediaType.Video)
        {
            return "Only videos can be downloaded for sharing.";
        }

        if (item.Status != MediaStatus.Ready || item.PlaybackKey is null)
        {
            return "This video isn't ready yet.";
        }

        if (item.IsRestricted || await media.IsSensitiveAsync(item.Id, cancellationToken))
        {
            return "Videos marked sensitive can't be downloaded for sharing.";
        }

        return null;
    }

    public static async Task<ShareCopyDto> DescribeAsync(
        ShareCopy? copy,
        MediaItem item,
        bool shareable,
        IMediaStorage storage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (copy is null || !shareable || !copy.IsCurrentFor(item))
        {
            return new ShareCopyDto(ShareCopyState.None);
        }

        if (copy.IsAvailable(now))
        {
            var url = await storage.CreateOriginalDownloadUrlAsync(copy.Key!, LinkLifetime, FileName, cancellationToken);
            return new ShareCopyDto(ShareCopyState.Ready, url, now + LinkLifetime);
        }

        if (copy.IsRendering(now))
        {
            return new ShareCopyDto(ShareCopyState.Rendering);
        }

        return new ShareCopyDto(copy.Status == ShareCopyStatus.Failed ? ShareCopyState.Failed : ShareCopyState.None);
    }
}

public sealed class GetShareCopyQueryHandler(
    IMediaRepository media,
    IShareCopyStore copies,
    IMediaStorage storage,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<GetShareCopyQuery, ShareCopyDto>
{
    public async Task<ShareCopyDto> Handle(GetShareCopyQuery request, CancellationToken cancellationToken)
    {
        var item = await ShareCopies.GetOwnAsync(media, caller, request.MediaId, cancellationToken);
        var shareable = await ShareCopies.WhyNotAsync(media, item, cancellationToken) is null;
        var copy = await copies.GetAsync(item.Id, cancellationToken);
        return await ShareCopies.DescribeAsync(copy, item, shareable, storage, clock.GetUtcNow(), cancellationToken);
    }
}

public sealed class RequestShareCopyCommandHandler(
    IMediaRepository media,
    IShareCopyStore copies,
    IMediaJobQueue queue,
    IMediaStorage storage,
    ICurrentUser caller,
    TimeProvider clock,
    ILogger<RequestShareCopyCommandHandler> logger)
    : IRequestHandler<RequestShareCopyCommand, ShareCopyDto>
{
    public async Task<ShareCopyDto> Handle(RequestShareCopyCommand request, CancellationToken cancellationToken)
    {
        var item = await ShareCopies.GetOwnAsync(media, caller, request.MediaId, cancellationToken);
        if (await ShareCopies.WhyNotAsync(media, item, cancellationToken) is { } reason)
        {
            throw new ValidationException([new ValidationFailure(nameof(request.MediaId), reason)]);
        }

        var now = clock.GetUtcNow();
        var copy = await copies.GetAsync(item.Id, cancellationToken);
        if (copy is not null && copy.IsCurrentFor(item) && (copy.IsAvailable(now) || copy.IsRendering(now)))
        {
            return await ShareCopies.DescribeAsync(copy, item, shareable: true, storage, now, cancellationToken);
        }

        if (copy is null)
        {
            copies.Add(ShareCopy.Start(item, now));
        }
        else
        {
            copy.Restart(item, now);
        }

        await copies.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(MediaProcessingJob.ForShareCopy(item), cancellationToken);
        logger.LogInformation("Queued a share copy of media {MediaId}.", item.Id);
        return new ShareCopyDto(ShareCopyState.Rendering);
    }
}
