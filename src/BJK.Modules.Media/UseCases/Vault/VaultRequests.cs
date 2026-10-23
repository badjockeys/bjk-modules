using System.Globalization;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.UseCases.Vault;

/// <summary>One clip in your vault.</summary>
/// <param name="Id">The clip.</param>
/// <param name="FileName">Its name on your device.</param>
/// <param name="MediaType">Video or Image.</param>
/// <param name="ContentType">Its type.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="Source">From a post, or uploaded straight to the vault.</param>
/// <param name="PostId">For clips from a post: the post, if known.</param>
/// <param name="CreatedAt">When it went into the vault.</param>
/// <param name="ExpiresAt">When it's deleted automatically (it follows your plan).</param>
public sealed record VaultClipDto(
    Guid Id,
    string FileName,
    MediaType MediaType,
    string ContentType,
    long SizeBytes,
    VaultClipSource Source,
    Guid? PostId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public static VaultClipDto From(VaultClip c) =>
        new(c.Id, c.FileName, c.MediaType, c.ContentType, c.SizeBytes, c.Source, c.PostId, c.CreatedAt, c.ExpiresAt);
}

/// <summary>Your vault: your plan's limits, the space used, and a page of your clips (newest first).</summary>
/// <param name="Plan">Your plan.</param>
/// <param name="UsedBytes">Space used.</param>
/// <param name="LimitBytes">Your plan's space.</param>
/// <param name="RetentionDays">How long your plan keeps clips.</param>
/// <param name="MaxFileBytes">The largest file you can upload straight to the vault.</param>
/// <param name="Clips">The clips.</param>
public sealed record VaultDto(
    SubscriptionPlan Plan,
    long UsedBytes,
    long LimitBytes,
    int RetentionDays,
    long MaxFileBytes,
    PagedResult<VaultClipDto> Clips);

/// <param name="ClipId">The new clip.</param>
/// <param name="Upload">PUT the file here, then call <c>POST /api/vault/{id}/complete</c>.</param>
public sealed record VaultUploadResult(Guid ClipId, SignedUpload Upload);

/// <param name="Url">Downloads the original (as <c>bad-jockeys-original-….ext</c>).</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record VaultDownloadDto(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Your vault (owner, 23 Oct 2026). Also records your plan, so your clips follow it.</summary>
public sealed record GetVaultQuery(int Page = 1, int PageSize = 20) : IRequest<VaultDto>;

/// <summary>Starts an upload straight to your vault, if it has room. PUT the file to the link, then complete it.</summary>
/// <param name="FileName">The name on your device.</param>
/// <param name="ContentType">A photo or video type.</param>
/// <param name="SizeBytes">Its size.</param>
public sealed record RequestVaultUploadCommand(string FileName, string ContentType, long SizeBytes) : IRequest<VaultUploadResult>;

/// <summary>The file has been uploaded: checks it arrived and fits, and keeps it.</summary>
public sealed record CompleteVaultUploadCommand(Guid ClipId) : IRequest<VaultClipDto>;

/// <summary>A short-lived link to download one of your clips.</summary>
public sealed record GetVaultDownloadQuery(Guid ClipId) : IRequest<VaultDownloadDto>;

/// <summary>Deletes one of your clips now.</summary>
public sealed record DeleteVaultClipCommand(Guid ClipId) : IRequest;

public sealed class GetVaultQueryValidator : AbstractValidator<GetVaultQuery>
{
    public GetVaultQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class RequestVaultUploadCommandValidator : AbstractValidator<RequestVaultUploadCommand>
{
    public RequestVaultUploadCommandValidator(IOptions<VaultOptions> options)
    {
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(1024);
        RuleFor(c => c.ContentType)
            .NotEmpty()
            .Must(type => MediaFormats.TryGet(type, out _, out _))
            .WithMessage("Add a video (MP4, MOV, M4V, 3GP, WebM, MKV, AVI) or a photo (JPEG, PNG, HEIC, WebP, AVIF, GIF, TIFF, BMP).");
        RuleFor(c => c.SizeBytes)
            .GreaterThan(0)
            .LessThanOrEqualTo(options.Value.MaxFileBytes)
            .WithMessage($"That file is too big for the vault: the most is {VaultSizes.Describe(options.Value.MaxFileBytes)}.");
    }
}

internal static class VaultSizes
{
    /// <summary>"2 GB", "512 MB".</summary>
    public static string Describe(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0} MB");

    /// <summary>The vault's file name for a download: ours, never the uploader's.</summary>
    public static string DownloadName(VaultClip clip) => string.Create(
        CultureInfo.InvariantCulture,
        $"bad-jockeys-original-{clip.CreatedAt:yyyy-MM-dd-HHmm}.{(MediaFormats.TryGet(clip.ContentType, out _, out var ext) ? ext : "bin")}");

    public static ValidationException Full(long limit) => new(
    [
        new ValidationFailure(
            "SizeBytes",
            $"Your vault is full ({Describe(limit)} on your plan). Delete some clips, or upgrade for more space."),
    ]);
}

internal static class VaultAccess
{
    /// <summary>The caller's own clip, or 404 (so nobody can probe for other people's clips).</summary>
    public static async Task<VaultClip> GetOwnAsync(IVaultStore store, ICurrentUser caller, Guid clipId, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var clip = await store.GetAsync(clipId, cancellationToken);
        return clip is not null && clip.IsOwnedBy(userId) ? clip : throw new NotFoundException("Vault clip", clipId);
    }

    /// <summary>The caller's plan now (Free if the token says nothing), recorded for the vault.</summary>
    public static async Task<SubscriptionPlan> SeePlanAsync(VaultPlans plans, IPlanResolver resolver, Guid userId, CancellationToken cancellationToken) =>
        await plans.SeeAsync(userId, resolver.CurrentPlan() ?? SubscriptionPlan.Free, cancellationToken);
}

public sealed class GetVaultQueryHandler(
    IVaultStore store,
    VaultPlans plans,
    IPlanResolver resolver,
    ICurrentUser caller,
    IOptions<VaultOptions> options)
    : IRequestHandler<GetVaultQuery, VaultDto>
{
    public async Task<VaultDto> Handle(GetVaultQuery request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var plan = await VaultAccess.SeePlanAsync(plans, resolver, userId, cancellationToken);
        var (items, total) = await store.ListAsync(userId, request.Page, request.PageSize, cancellationToken);
        var limits = options.Value;
        return new VaultDto(
            plan,
            await store.UsedBytesAsync(userId, cancellationToken),
            limits.QuotaFor(plan),
            (int)limits.RetentionFor(plan).TotalDays,
            limits.MaxFileBytes,
            new PagedResult<VaultClipDto>(items.Select(VaultClipDto.From).ToList(), request.Page, request.PageSize, total));
    }
}

public sealed class RequestVaultUploadCommandHandler(
    IVaultStore store,
    IMediaStorage storage,
    VaultPlans plans,
    IPlanResolver resolver,
    ICurrentUser caller,
    IOptions<VaultOptions> options,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider clock)
    : IRequestHandler<RequestVaultUploadCommand, VaultUploadResult>
{
    public async Task<VaultUploadResult> Handle(RequestVaultUploadCommand request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var plan = await VaultAccess.SeePlanAsync(plans, resolver, userId, cancellationToken);
        var limit = options.Value.QuotaFor(plan);
        if (await store.UsedBytesAsync(userId, cancellationToken) + request.SizeBytes > limit)
        {
            throw VaultSizes.Full(limit);
        }

        var clip = VaultClip.StartUpload(userId, request.FileName, request.ContentType, request.SizeBytes, clock.GetUtcNow(), options.Value.RetentionFor(plan));
        var upload = await storage.CreateUploadAsync(MediaBucket.Vault, clip.Key, clip.ContentType, mediaOptions.Value.UploadUrlLifetime, cancellationToken);
        store.Add(clip);
        await store.SaveChangesAsync(cancellationToken);
        return new VaultUploadResult(clip.Id, upload);
    }
}

public sealed class CompleteVaultUploadCommandHandler(
    IVaultStore store,
    IMediaStorage storage,
    VaultPlans plans,
    IPlanResolver resolver,
    ICurrentUser caller,
    IOptions<VaultOptions> options,
    TimeProvider clock)
    : IRequestHandler<CompleteVaultUploadCommand, VaultClipDto>
{
    public async Task<VaultClipDto> Handle(CompleteVaultUploadCommand request, CancellationToken cancellationToken)
    {
        var clip = await VaultAccess.GetOwnAsync(store, caller, request.ClipId, cancellationToken);
        if (clip.Status == VaultClipStatus.Stored)
        {
            return VaultClipDto.From(clip);
        }

        var file = await storage.FindAsync(MediaBucket.Vault, clip.Key, cancellationToken)
            ?? throw new ValidationException([new ValidationFailure(nameof(request.ClipId), "The file hasn't arrived yet. Try uploading it again.")]);

        // The real size has to fit too: a file bigger than declared mustn't slip past the limits.
        var plan = await VaultAccess.SeePlanAsync(plans, resolver, clip.UserId, cancellationToken);
        var limit = options.Value.QuotaFor(plan);
        var usedByOthers = await store.UsedBytesAsync(clip.UserId, cancellationToken) - clip.SizeBytes;
        if (file.SizeBytes > options.Value.MaxFileBytes || usedByOthers + file.SizeBytes > limit)
        {
            await storage.DeleteAsync(MediaBucket.Vault, [clip.Key], cancellationToken);
            store.Remove(clip);
            await store.SaveChangesAsync(cancellationToken);
            throw file.SizeBytes > options.Value.MaxFileBytes
                ? new ValidationException([new ValidationFailure("SizeBytes", $"That file is too big for the vault: the most is {VaultSizes.Describe(options.Value.MaxFileBytes)}.")])
                : VaultSizes.Full(limit);
        }

        clip.MarkStored(file.SizeBytes, clock.GetUtcNow());
        await store.SaveChangesAsync(cancellationToken);
        return VaultClipDto.From(clip);
    }
}

public sealed class GetVaultDownloadQueryHandler(IVaultStore store, IMediaStorage storage, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<GetVaultDownloadQuery, VaultDownloadDto>
{
    /// <summary>How long a download link works.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    public async Task<VaultDownloadDto> Handle(GetVaultDownloadQuery request, CancellationToken cancellationToken)
    {
        var clip = await VaultAccess.GetOwnAsync(store, caller, request.ClipId, cancellationToken);
        if (clip.Status != VaultClipStatus.Stored)
        {
            throw new NotFoundException("Vault clip", request.ClipId);
        }

        var url = await storage.CreateDownloadUrlAsync(MediaBucket.Vault, clip.Key, LinkLifetime, VaultSizes.DownloadName(clip), cancellationToken);
        return new VaultDownloadDto(url, clock.GetUtcNow() + LinkLifetime);
    }
}

public sealed class DeleteVaultClipCommandHandler(IVaultStore store, IMediaStorage storage, ICurrentUser caller)
    : IRequestHandler<DeleteVaultClipCommand>
{
    public async Task Handle(DeleteVaultClipCommand request, CancellationToken cancellationToken)
    {
        var clip = await VaultAccess.GetOwnAsync(store, caller, request.ClipId, cancellationToken);

        // File first: if that fails the clip is still listed and can be deleted again.
        await storage.DeleteAsync(MediaBucket.Vault, [clip.Key], cancellationToken);
        store.Remove(clip);
        await store.SaveChangesAsync(cancellationToken);
    }
}
