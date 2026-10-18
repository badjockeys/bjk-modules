using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;

namespace BJK.Modules.Media.UseCases;

internal static class MediaAccess
{
    /// <summary>The caller's account id from their token. Every upload is filed under it.</summary>
    public static Guid RequireUserId(ICurrentUser caller) =>
        Guid.TryParse(caller.IdentityId, out var id) && id != Guid.Empty
            ? id
            : throw new ForbiddenException("This needs a signed-in user's token.");

    public static bool IsAdmin(ICurrentUser caller) => caller.HasScope(MediaModule.ManageScope);

    /// <summary>Media the caller may change: their own, or anyone's with the manage scope.</summary>
    public static async Task<MediaItem> GetForChangeAsync(
        IMediaRepository media,
        ICurrentUser caller,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        var item = await media.GetByIdAsync(mediaId, cancellationToken)
            ?? throw new NotFoundException("Media", mediaId);

        var isOwner = Guid.TryParse(caller.IdentityId, out var userId) && item.IsOwnedBy(userId);
        if (!isOwner && !IsAdmin(caller))
        {
            throw new ForbiddenException("You can only change your own uploads.");
        }

        return item;
    }
}
