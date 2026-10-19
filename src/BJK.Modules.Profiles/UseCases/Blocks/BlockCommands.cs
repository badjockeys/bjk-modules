using BJK.Modules.Media.Contracts;
using BJK.Modules.Profiles.Domain;
using BJK.Modules.Profiles.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Profiles.UseCases.Blocks;

/// <summary>Someone the signed-in user has blocked. <c>Handle</c> is null if their profile no longer exists.</summary>
public sealed record BlockDto(Guid UserId, string? Handle, string? DisplayName, Uri? AvatarUrl, DateTimeOffset BlockedAt);

/// <summary>
/// Blocks a user, by @handle or user id. Their posts and comments are hidden from the signed-in user, and they can't
/// comment on or react to the signed-in user's posts. Blocking someone already blocked does nothing.
/// </summary>
public sealed record BlockUserCommand(string User) : IRequest<BlockDto>;

/// <summary>Unblocks a user, by @handle or user id. Does nothing if they weren't blocked.</summary>
public sealed record UnblockUserCommand(string User) : IRequest;

/// <summary>Everyone the signed-in user has blocked, most recent first.</summary>
public sealed record ListMyBlocksQuery : IRequest<IReadOnlyList<BlockDto>>;

public sealed class BlockUserCommandValidator : AbstractValidator<BlockUserCommand>
{
    public BlockUserCommandValidator()
    {
        RuleFor(c => c.User).NotEmpty();
    }
}

public sealed class BlockUserCommandHandler(
    IProfileRepository profiles,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<BlockUserCommand, BlockDto>
{
    public async Task<BlockDto> Handle(BlockUserCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var target = await BlockTargets.FindAsync(profiles, request.User, cancellationToken);
        if (target is not { IsActive: true })
        {
            throw new NotFoundException("Profile", request.User);
        }

        if (target.Id == userId)
        {
            throw ProfileRules.Invalid(nameof(request.User), "You can't block yourself.");
        }

        var block = await profiles.GetBlockAsync(userId, target.Id, cancellationToken);
        if (block is null)
        {
            if (await profiles.CountBlocksAsync(userId, cancellationToken) >= Block.MaxPerUser)
            {
                throw ProfileRules.Invalid(nameof(request.User), $"You can block up to {Block.MaxPerUser} people. Unblock someone first.");
            }

            block = Block.Create(userId, target.Id, clock.GetUtcNow());
            profiles.AddBlock(block);
            await profiles.SaveChangesAsync(cancellationToken);
        }

        var avatars = await Avatars.GetUrlsAsync(media, [target], cancellationToken);
        return new BlockDto(target.Id, target.Handle, target.DisplayName, Avatars.UrlFor(target, avatars), block.CreatedAt);
    }
}

public sealed class UnblockUserCommandHandler(IProfileRepository profiles, ICurrentUser caller)
    : IRequestHandler<UnblockUserCommand>
{
    public async Task Handle(UnblockUserCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        Guid? blockedId = Guid.TryParse(request.User, out var id)
            ? id
            : (await BlockTargets.FindAsync(profiles, request.User, cancellationToken))?.Id;
        if (blockedId is null)
        {
            return;
        }

        var block = await profiles.GetBlockAsync(userId, blockedId.Value, cancellationToken);
        if (block is null)
        {
            return;
        }

        profiles.RemoveBlock(block);
        await profiles.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ListMyBlocksQueryHandler(IProfileRepository profiles, IMediaDirectory media, ICurrentUser caller)
    : IRequestHandler<ListMyBlocksQuery, IReadOnlyList<BlockDto>>
{
    public async Task<IReadOnlyList<BlockDto>> Handle(ListMyBlocksQuery request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        var blocks = await profiles.ListBlocksAsync(userId, cancellationToken);
        if (blocks.Count == 0)
        {
            return [];
        }

        var found = (await profiles.GetManyAsync(blocks.Select(b => b.BlockedId).ToList(), cancellationToken))
            .ToDictionary(p => p.Id);
        var avatars = await Avatars.GetUrlsAsync(media, found.Values.ToList(), cancellationToken);

        return blocks
            .Select(b => found.TryGetValue(b.BlockedId, out var p)
                ? new BlockDto(b.BlockedId, p.Handle, p.DisplayName, Avatars.UrlFor(p, avatars), b.CreatedAt)
                : new BlockDto(b.BlockedId, null, null, null, b.CreatedAt))
            .ToList();
    }
}

internal static class BlockTargets
{
    /// <summary>The profile named by a user id or an @handle, or null.</summary>
    public static async Task<Profile?> FindAsync(IProfileRepository profiles, string user, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(user, out var id))
        {
            return await profiles.GetByIdAsync(id, cancellationToken);
        }

        var handle = Handle.Normalise(user);
        return Handle.IsValid(handle) ? await profiles.GetByHandleAsync(handle, cancellationToken) : null;
    }
}
