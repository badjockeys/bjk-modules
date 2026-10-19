using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;

namespace BJK.Modules.Media.UseCases.Delete;

/// <summary>
/// Deletes the files (original and processed) and marks the record Deleted. The row is kept for
/// audit; the files themselves are gone.
/// </summary>
/// <param name="MediaId">The media to delete.</param>
/// <param name="AsModerator">True on the admin endpoint: a moderator can delete anyone's media.</param>
/// <param name="Reason">Required on the admin endpoint; recorded in the moderation audit log.</param>
public sealed record DeleteMediaCommand(Guid MediaId, bool AsModerator = false, string? Reason = null) : IRequest;

public sealed class DeleteMediaCommandValidator : AbstractValidator<DeleteMediaCommand>
{
    public const int ReasonMaxLength = 500;

    public DeleteMediaCommandValidator()
    {
        When(c => c.AsModerator, () => RuleFor(c => c.Reason)
            .NotEmpty()
            .WithMessage("Give a reason. It's recorded in the moderation audit log.")
            .MaximumLength(ReasonMaxLength));
    }
}

public sealed class DeleteMediaCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    IModerationAuditLog audit,
    ICurrentUser caller,
    TimeProvider clock)
    : IRequestHandler<DeleteMediaCommand>
{
    public async Task Handle(DeleteMediaCommand request, CancellationToken cancellationToken)
    {
        var item = await MediaAccess.GetForChangeAsync(media, caller, request.MediaId, cancellationToken, request.AsModerator);

        // Remove the files first: if that fails the record is untouched and the delete can be retried.
        await MediaFiles.DeleteAllAsync(storage, item, cancellationToken);

        var before = item.Status;
        item.Delete(clock.GetUtcNow());
        await media.SaveChangesAsync(cancellationToken);

        if (request.AsModerator)
        {
            await audit.RecordAsync(
                new ModerationAuditEntry(
                    "Media",
                    ModerationActions.DeleteMedia,
                    "Media",
                    item.Id.ToString(),
                    (request.Reason ?? string.Empty).Trim(),
                    $"Status={before}",
                    $"Status={item.Status}"),
                cancellationToken);
        }
    }
}
