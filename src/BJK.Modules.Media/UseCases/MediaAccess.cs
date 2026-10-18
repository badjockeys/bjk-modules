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

    /// <summary>
    /// Media the caller may change: their own on the public endpoints, or anyone's for a moderator on the admin
    /// endpoints (<paramref name="asModerator"/>).
    /// </summary>
    public static async Task<MediaItem> GetForChangeAsync(
        IMediaRepository media,
        ICurrentUser caller,
        Guid mediaId,
        CancellationToken cancellationToken,
        bool asModerator = false)
    {
        if (asModerator)
        {
            caller.RequireModerator(MediaModule.ManageScope);
        }

        var item = await media.GetByIdAsync(mediaId, cancellationToken)
            ?? throw new NotFoundException("Media", mediaId);

        var isOwner = Guid.TryParse(caller.IdentityId, out var userId) && item.IsOwnedBy(userId);
        if (!isOwner && !asModerator)
        {
            throw new ForbiddenException("You can only change your own uploads.");
        }

        return item;
    }
}
