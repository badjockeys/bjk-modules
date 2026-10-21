using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Reports;
using BJK.Modules.Shared;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace BJK.Modules.Posts.UseCases.Moderation;

/// <summary>A blocked web address.</summary>
public sealed record BlockedUrlDto(Guid Id, string Pattern, string Source, string? Reference, DateTimeOffset AddedAt, Guid? AddedBy)
{
    internal static BlockedUrlDto From(BlockedUrl b) => new(b.Id, b.Pattern, b.Source, b.Reference, b.AddedAt, b.AddedBy);
}

/// <summary>Blocked addresses, newest first, optionally one source's.</summary>
public sealed record ListBlockedUrlsQuery(string? Source = null, int Page = 1, int PageSize = 50) : IRequest<PagedResult<BlockedUrlDto>>;

public sealed class ListBlockedUrlsQueryHandler(IBlockedUrlRepository urls, ICurrentUser caller)
    : IRequestHandler<ListBlockedUrlsQuery, PagedResult<BlockedUrlDto>>
{
    public async Task<PagedResult<BlockedUrlDto>> Handle(ListBlockedUrlsQuery request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var (items, total) = await urls.ListAsync(request.Source, (page - 1) * size, size, cancellationToken);
        return new PagedResult<BlockedUrlDto>(items.Select(BlockedUrlDto.From).ToList(), page, size, total);
    }
}

/// <summary>
/// Blocks a web address: posts and comments containing it are held for review. A bare host ("example.com") covers
/// the whole site and its subdomains. Takes effect within <c>BlockedUrls:RefreshInterval</c> (5 minutes).
/// </summary>
public sealed record AddBlockedUrlCommand(string Address, string Reason) : IRequest<BlockedUrlDto>;

public sealed class AddBlockedUrlCommandValidator : AbstractValidator<AddBlockedUrlCommand>
{
    public AddBlockedUrlCommandValidator()
    {
        RuleFor(c => c.Address).Must(a => BlockedUrls.Normalise(a) is { Length: <= BlockedUrl.PatternMaxLength })
            .WithMessage("Enter a web address, e.g. example.com or https://example.com/page.");
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(BlockedUrl.ReferenceMaxLength);
    }
}

public sealed class AddBlockedUrlCommandHandler(IBlockedUrlRepository urls, IModerationAuditLog audit, ICurrentUser caller, TimeProvider clock)
    : IRequestHandler<AddBlockedUrlCommand, BlockedUrlDto>
{
    public async Task<BlockedUrlDto> Handle(AddBlockedUrlCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        var url = new BlockedUrl(request.Address, BlockedUrl.ModerationSource, request.Reason.Trim(), clock.GetUtcNow(), caller.UserId());
        if (await urls.ExistsAsync(url.Source, url.Pattern, cancellationToken))
        {
            throw new ValidationException([new ValidationFailure(nameof(request.Address), "That address is already blocked.")]);
        }

        urls.Add(url);
        await urls.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry("Posts", ModerationActions.BlockUrl, "BlockedUrl", url.Id.ToString(), request.Reason.Trim(), null, url.Pattern),
            cancellationToken);
        return BlockedUrlDto.From(url);
    }
}

/// <summary>Unblocks an address added by moderators or for testing (a synced list's entries come back with its next sync).</summary>
public sealed record RemoveBlockedUrlCommand(Guid Id, string Reason) : IRequest;

public sealed class RemoveBlockedUrlCommandHandler(IBlockedUrlRepository urls, IModerationAuditLog audit, ICurrentUser caller)
    : IRequestHandler<RemoveBlockedUrlCommand>
{
    public async Task Handle(RemoveBlockedUrlCommand request, CancellationToken cancellationToken)
    {
        ReportRules.RequireModerator(caller);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ValidationException([new ValidationFailure(nameof(request.Reason), "Give a reason.")]);
        }

        var url = await urls.GetAsync(request.Id, cancellationToken) ?? throw new NotFoundException("Blocked address", request.Id);
        if (url.Source is not (BlockedUrl.ModerationSource or BlockedUrl.TestSource))
        {
            throw new ValidationException([new ValidationFailure(nameof(request.Id), $"This address is on the {url.Source} list and would come back with its next sync.")]);
        }

        urls.Remove(url);
        await urls.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(
            new ModerationAuditEntry("Posts", ModerationActions.UnblockUrl, "BlockedUrl", url.Id.ToString(), request.Reason.Trim(), url.Pattern, null),
            cancellationToken);
    }
}
