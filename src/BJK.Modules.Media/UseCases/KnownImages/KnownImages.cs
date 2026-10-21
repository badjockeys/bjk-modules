using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.UseCases.Review;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Media.UseCases.KnownImages;

/// <summary>A hash on the known-image list.</summary>
public sealed record KnownImageHashDto(Guid Id, KnownHashKind Kind, string Value, string Source, string? Reference, DateTimeOffset AddedAt, Guid? AddedBy)
{
    internal static KnownImageHashDto From(KnownImageHash h) => new(h.Id, h.Kind, h.Value, h.Source, h.Reference, h.AddedAt, h.AddedBy);
}

/// <summary>An upload blocked because it matched a known image. No picture is kept.</summary>
public sealed record KnownImageMatchDto(
    Guid Id,
    Guid MediaId,
    Guid UserId,
    KnownHashKind Kind,
    string Source,
    string? Reference,
    int? Distance,
    string? Sha256,
    DateTimeOffset MatchedAt,
    DateTimeOffset? ReportedAt,
    string? ReportReference)
{
    internal static KnownImageMatchDto From(KnownImageMatch m) =>
        new(m.Id, m.MediaId, m.UserId, m.Kind, m.Source, m.Reference, m.Distance, m.Sha256, m.MatchedAt, m.ReportedAt, m.ReportReference);
}

/// <summary>Blocked uploads, newest first. Admins only.</summary>
public sealed record ListKnownImageMatchesQuery(int Page = 1, int PageSize = 20) : IRequest<PagedResult<KnownImageMatchDto>>;

public sealed class ListKnownImageMatchesQueryHandler(IKnownImageRepository known, ICurrentUser caller)
    : IRequestHandler<ListKnownImageMatchesQuery, PagedResult<KnownImageMatchDto>>
{
    public async Task<PagedResult<KnownImageMatchDto>> Handle(ListKnownImageMatchesQuery request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var (page, size) = KnownImagePaging.Clamp(request.Page, request.PageSize);
        var (items, total) = await known.ListMatchesAsync((page - 1) * size, size, cancellationToken);
        return new PagedResult<KnownImageMatchDto>(items.Select(KnownImageMatchDto.From).ToList(), page, size, total);
    }
}

/// <summary>Records that a match was reported to the authorities (e.g. the NCA or the IWF), with their reference.</summary>
public sealed record MarkKnownImageMatchReportedCommand(Guid MatchId, string Reference) : IRequest<KnownImageMatchDto>;

public sealed class MarkKnownImageMatchReportedCommandValidator : AbstractValidator<MarkKnownImageMatchReportedCommand>
{
    public MarkKnownImageMatchReportedCommandValidator()
    {
        RuleFor(c => c.Reference).NotEmpty().MaximumLength(KnownImageHash.ReferenceMaxLength);
    }
}

public sealed class MarkKnownImageMatchReportedCommandHandler(
    IKnownImageRepository known,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<MarkKnownImageMatchReportedCommand, KnownImageMatchDto>
{
    public async Task<KnownImageMatchDto> Handle(MarkKnownImageMatchReportedCommand request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var match = await known.GetMatchAsync(request.MatchId, cancellationToken)
            ?? throw new NotFoundException("Known image match", request.MatchId);
        var before = match.ReportReference;
        match.MarkReported(request.Reference.Trim(), clock.GetUtcNow());
        await known.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Media",
                ModerationActions.ReportKnownImageMatch,
                "KnownImageMatch",
                match.Id.ToString(),
                $"Reported (reference {match.ReportReference}).",
                before is null ? "Reported=false" : $"Reference={before}",
                $"Reference={match.ReportReference}"),
            cancellationToken);
        return KnownImageMatchDto.From(match);
    }
}

/// <summary>The known-image hashes, newest first, optionally one source's. Admins only.</summary>
public sealed record ListKnownImageHashesQuery(string? Source = null, int Page = 1, int PageSize = 50) : IRequest<PagedResult<KnownImageHashDto>>;

public sealed class ListKnownImageHashesQueryHandler(IKnownImageRepository known, ICurrentUser caller)
    : IRequestHandler<ListKnownImageHashesQuery, PagedResult<KnownImageHashDto>>
{
    public async Task<PagedResult<KnownImageHashDto>> Handle(ListKnownImageHashesQuery request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var (page, size) = KnownImagePaging.Clamp(request.Page, request.PageSize);
        var (items, total) = await known.ListHashesAsync(request.Source, (page - 1) * size, size, cancellationToken);
        return new PagedResult<KnownImageHashDto>(items.Select(KnownImageHashDto.From).ToList(), page, size, total);
    }
}

/// <summary>
/// Adds a hash by hand (source <see cref="KnownImageHash.ModerationSource"/>), e.g. one given by the police or a hotline.
/// Admins only. Workers pick it up within <c>Media:KnownImages:RefreshInterval</c>.
/// </summary>
public sealed record AddKnownImageHashCommand(KnownHashKind Kind, string Value, string? Reference) : IRequest<KnownImageHashDto>;

public sealed class AddKnownImageHashCommandValidator : AbstractValidator<AddKnownImageHashCommand>
{
    public AddKnownImageHashCommandValidator()
    {
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.Value).Must((c, v) => KnownImageHash.Normalise(c.Kind, v) is not null)
            .WithMessage(c => c.Kind == KnownHashKind.Md5 ? "An MD5 hash is 32 hex digits." : $"A {c.Kind} hash is 64 hex digits.");
        RuleFor(c => c.Reference).MaximumLength(KnownImageHash.ReferenceMaxLength);
    }
}

public sealed class AddKnownImageHashCommandHandler(
    IKnownImageRepository known,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<AddKnownImageHashCommand, KnownImageHashDto>
{
    public async Task<KnownImageHashDto> Handle(AddKnownImageHashCommand request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var value = KnownImageHash.Normalise(request.Kind, request.Value)!;
        if (await known.HasHashAsync(KnownImageHash.ModerationSource, request.Kind, value, cancellationToken))
        {
            throw MediaReview.Invalid(nameof(request.Value), "That hash is already on the list.");
        }

        var hash = new KnownImageHash(
            request.Kind, value, KnownImageHash.ModerationSource, request.Reference?.Trim(), clock.GetUtcNow(), caller.UserId());
        known.Add(hash);
        await known.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry("Media", ModerationActions.AddKnownImageHash, "KnownImageHash", hash.Id.ToString(), $"{hash.Kind} {hash.Value}", null, null),
            cancellationToken);
        return KnownImageHashDto.From(hash);
    }
}

/// <summary>
/// Removes a hash added by moderators or for testing. Hashes from a synced list can't be removed here (the next sync
/// would add them back); ask the list's provider. Admins only.
/// </summary>
public sealed record RemoveKnownImageHashCommand(Guid HashId) : IRequest;

public sealed class RemoveKnownImageHashCommandHandler(IKnownImageRepository known, IModerationAuditLog audit, ICurrentUser caller)
    : IRequestHandler<RemoveKnownImageHashCommand>
{
    public async Task Handle(RemoveKnownImageHashCommand request, CancellationToken cancellationToken)
    {
        caller.RequireAdmin(MediaModule.ManageScope);
        var hash = await known.GetHashAsync(request.HashId, cancellationToken)
            ?? throw new NotFoundException("Known image hash", request.HashId);
        if (hash.Source is not (KnownImageHash.ModerationSource or KnownImageHash.TestSource))
        {
            throw MediaReview.Invalid(nameof(request.HashId), $"This hash comes from the {hash.Source} list and would be added back by the next sync.");
        }

        known.Remove(hash);
        await known.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry("Media", ModerationActions.RemoveKnownImageHash, "KnownImageHash", hash.Id.ToString(), $"{hash.Kind} {hash.Value}", null, null),
            cancellationToken);
    }
}

/// <summary>
/// After removing an image, a moderator blocks copies of it: its file hash and the perceptual hashes recorded when it
/// was processed go on the known-image list (source <see cref="KnownImageHash.ModerationSource"/>), so the same picture,
/// or a resized or recompressed copy, is blocked if anyone uploads it again.
/// </summary>
/// <param name="MediaId">The media.</param>
/// <param name="Reason">Why, for the audit log.</param>
public sealed record BlockMediaCopiesCommand(Guid MediaId, string Reason) : IRequest<int>;

public sealed class BlockMediaCopiesCommandValidator : AbstractValidator<BlockMediaCopiesCommand>
{
    public BlockMediaCopiesCommandValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class BlockMediaCopiesCommandHandler(
    IMediaRepository media,
    IKnownImageRepository known,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<BlockMediaCopiesCommand, int>
{
    /// <returns>How many hashes were added.</returns>
    public async Task<int> Handle(BlockMediaCopiesCommand request, CancellationToken cancellationToken)
    {
        caller.RequireModerator(MediaModule.ManageScope);
        var item = await MediaReview.GetAsync(media, request.MediaId, cancellationToken);
        var wanted = new List<(KnownHashKind Kind, string Value)>();
        if (item.Sha256 is { } sha)
        {
            wanted.Add((KnownHashKind.Sha256, sha));
        }

        wanted.AddRange((item.PerceptualHashes ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => (KnownHashKind.Pdq, h)));
        if (wanted.Count == 0)
        {
            throw MediaReview.Invalid(nameof(request.MediaId), "This media has no recorded hashes (it hasn't been processed).");
        }

        var added = 0;
        var now = clock.GetUtcNow();
        foreach (var (kind, value) in wanted.Distinct())
        {
            if (KnownImageHash.Normalise(kind, value) is { } normalised
                && !await known.HasHashAsync(KnownImageHash.ModerationSource, kind, normalised, cancellationToken))
            {
                known.Add(new KnownImageHash(kind, normalised, KnownImageHash.ModerationSource, $"media:{item.Id:N}", now, caller.UserId()));
                added++;
            }
        }

        await known.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry(
                "Media",
                ModerationActions.BlockMediaCopies,
                "Media",
                item.Id.ToString(),
                request.Reason.Trim(),
                null,
                $"HashesAdded={added}"),
            cancellationToken);
        return added;
    }
}

internal static class KnownImagePaging
{
    public static (int Page, int Size) Clamp(int page, int size) => (Math.Max(1, page), Math.Clamp(size, 1, 200));
}
