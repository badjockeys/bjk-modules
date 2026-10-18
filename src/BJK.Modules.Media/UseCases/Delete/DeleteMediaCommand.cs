using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Security;
using MediatR;

namespace BJK.Modules.Media.UseCases.Delete;

/// <summary>
/// Deletes the files (original and processed) and marks the record Deleted. The row is kept for
/// audit; the files themselves are gone.
/// </summary>
/// <param name="MediaId">The media to delete.</param>
/// <param name="AsModerator">True on the admin endpoint: a moderator can delete anyone's media.</param>
public sealed record DeleteMediaCommand(Guid MediaId, bool AsModerator = false) : IRequest;

public sealed class DeleteMediaCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DeleteMediaCommand>
{
    public async Task Handle(DeleteMediaCommand request, CancellationToken cancellationToken)
    {
        var item = await MediaAccess.GetForChangeAsync(media, caller, request.MediaId, cancellationToken, request.AsModerator);

        // Remove the files first: if that fails the record is untouched and the delete can be retried.
        await MediaFiles.DeleteAllAsync(storage, item, cancellationToken);

        item.Delete(clock.GetUtcNow());
        await media.SaveChangesAsync(cancellationToken);
    }
}
