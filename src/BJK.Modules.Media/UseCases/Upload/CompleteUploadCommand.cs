using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Security;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.UseCases.Upload;

/// <summary>
/// Step 2 of an upload: the app says the file has been sent. We check it really is in storage and
/// within the size limit, then queue it for processing.
/// </summary>
public sealed record CompleteUploadCommand(Guid MediaId) : IRequest<MediaDto>;

public sealed class CompleteUploadCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IMediaJobQueue queue,
    ICurrentUser caller,
    IOptions<MediaOptions> options,
    TimeProvider clock)
    : IRequestHandler<CompleteUploadCommand, MediaDto>
{
    public async Task<MediaDto> Handle(CompleteUploadCommand request, CancellationToken cancellationToken)
    {
        var item = await MediaAccess.GetForChangeAsync(media, caller, request.MediaId, cancellationToken);

        // Already completed (e.g. the app retried after a timeout): nothing more to do.
        if (item.Status != MediaStatus.Uploading)
        {
            return MediaDto.From(item, storage);
        }

        var file = await storage.FindOriginalAsync(item.OriginalKey, cancellationToken)
            ?? throw Invalid("The file hasn't arrived yet. Finish the upload, then try again.");

        var limit = options.Value.MaxBytesFor(item.MediaType);
        if (file.SizeBytes > limit)
        {
            await storage.DeleteOriginalAsync(item.OriginalKey, cancellationToken);
            item.MarkFailed(options.Value.TooBigMessage(item.MediaType, file.SizeBytes), clock.GetUtcNow());
            await media.SaveChangesAsync(cancellationToken);
            throw Invalid(item.FailureReason!);
        }

        item.MarkUploaded(file.SizeBytes, clock.GetUtcNow(), options.Value.OriginalRetention);

        // Queue first, then save: if queueing fails the media stays Uploading and the app can retry.
        // If saving fails after queueing, the worker still finds the file and processes it.
        await queue.EnqueueAsync(
            new MediaProcessingJob(item.Id, item.UserId, item.MediaType, item.OriginalKey, item.OriginalContentType),
            cancellationToken);
        await media.SaveChangesAsync(cancellationToken);

        return MediaDto.From(item, storage);
    }

    private static ValidationException Invalid(string message) =>
        new([new ValidationFailure(nameof(CompleteUploadCommand.MediaId), message)]);
}
