using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Shared.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Media.UseCases.Upload;

/// <summary>
/// Step 1 of an upload: records the media and returns a short-lived link the app uses to send the
/// file straight to storage. The file never passes through the API.
/// </summary>
/// <param name="FileName">The name on the user's device. Stored for display only.</param>
/// <param name="ContentType">e.g. video/mp4, video/quicktime, image/jpeg, image/heic.</param>
/// <param name="SizeBytes">Size the app is about to upload. Checked again once the file arrives.</param>
public sealed record RequestUploadCommand(string FileName, string ContentType, long SizeBytes)
    : IRequest<RequestUploadResult>;

public sealed record RequestUploadResult(Guid MediaId, MediaType MediaType, SignedUpload Upload);

public sealed class RequestUploadCommandValidator : AbstractValidator<RequestUploadCommand>
{
    public RequestUploadCommandValidator(IOptions<MediaOptions> options)
    {
        var limits = options.Value;

        RuleFor(c => c.FileName).NotEmpty().MaximumLength(MediaItem.OriginalFileNameMaxLength);

        RuleFor(c => c.ContentType)
            .NotEmpty()
            .Must(type => MediaFormats.TryGet(type, out _, out _))
            .WithMessage($"Upload a video (MP4, MOV, 3GP) or a photo (JPEG, PNG, HEIC, WebP). Supported types: {string.Join(", ", MediaFormats.ContentTypes)}.");

        RuleFor(c => c.SizeBytes)
            .GreaterThan(0)
            .Must((command, size) =>
                !MediaFormats.TryGet(command.ContentType, out var type, out _) || size <= limits.MaxBytesFor(type))
            .WithMessage(command => limits.TooBigMessage(
                MediaFormats.TryGet(command.ContentType, out var type, out _) ? type : MediaType.Image,
                command.SizeBytes));
    }
}

public sealed class RequestUploadCommandHandler(
    IMediaRepository media,
    IMediaStorage storage,
    ICurrentUser caller,
    IOptions<MediaOptions> options,
    TimeProvider clock)
    : IRequestHandler<RequestUploadCommand, RequestUploadResult>
{
    public async Task<RequestUploadResult> Handle(RequestUploadCommand request, CancellationToken cancellationToken)
    {
        var userId = MediaAccess.RequireUserId(caller);
        var item = MediaItem.Create(userId, request.FileName, request.ContentType, clock.GetUtcNow());

        var upload = await storage.CreateOriginalUploadAsync(
            item.OriginalKey,
            item.OriginalContentType,
            options.Value.UploadUrlLifetime,
            cancellationToken);

        media.Add(item);
        await media.SaveChangesAsync(cancellationToken);

        return new RequestUploadResult(item.Id, item.MediaType, upload);
    }
}
