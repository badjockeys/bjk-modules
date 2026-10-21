using BJK.Modules.Media.Contracts;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Passengers;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Notifications;
using MediatR;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Update;

/// <summary>
/// Edits a post's details. Its photos and videos can't be changed. The author only. The text is screened again.
/// <c>IsSensitive</c>: true or false to change the sensitive tag, null to leave it. Authors can only remove a tag they
/// set themselves, not a moderator's or an automatic one (400). Tagging moves the media to private storage (blurred
/// previews public) within seconds; untagging publishes them again. <c>Types</c> replace the post's types of bad
/// driving (or send one <c>Category</c>). <c>Passengers</c> replaces the tagged @handles (null leaves them); new ones
/// are asked to accept. Changing the types to only bad parking removes every passenger.
/// </summary>
public sealed record UpdatePostCommand(
    Guid PostId,
    string Title,
    string? Description,
    IncidentCategory? Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    bool? IsSensitive = null,
    IReadOnlyList<IncidentCategory>? Types = null,
    IReadOnlyList<string>? Passengers = null,
    VehicleType? VehicleType = null,
    string? VehicleMake = null,
    string? VehicleModel = null,
    VehicleColour? VehicleColour = null,
    bool? ReportedToPolice = null) : IRequest<PostDto>, IPostDetails, IRequiresAgeVerification;

public sealed class UpdatePostCommandValidator(TimeProvider clock, IOptions<PostsOptions> options)
    : PostDetailsValidator<UpdatePostCommand>(clock, options);

public sealed class UpdatePostCommandHandler(
    IPostRepository posts,
    IContentScreener screener,
    PostViews views,
    IMediaDirectory media,
    ICurrentUser caller,
    TimeProvider clock,
    PassengerTagging? passengers = null,
    INotificationSender? notifications = null,
    Hashtags.HashtagSync? hashtags = null)
    : IRequestHandler<UpdatePostCommand, PostDto>
{
    public async Task<PostDto> Handle(UpdatePostCommand request, CancellationToken cancellationToken)
    {
        var post = await PostAccess.GetForChangeAsync(posts, caller, request.PostId, cancellationToken);

        if (request.IsSensitive == false && post.IsSensitive && !post.AuthorMayRemoveSensitive)
        {
            throw PostAccess.Invalid(
                nameof(request.IsSensitive),
                post.SensitiveSource == PostSensitiveSource.Moderator
                    ? "A moderator marked this post sensitive, so you can't remove the tag."
                    : "Our checks found graphic content in this post, so you can't remove the sensitive tag yourself. A moderator reviews it.");
        }

        var now = clock.GetUtcNow();
        var details = request.ToDetails();
        var passengerIds = passengers is null || request.Passengers is null
            ? null
            : await passengers.ResolveAsync(post.AuthorId, request.Passengers, details.Types!, cancellationToken);
        post.Update(details, now);
        IReadOnlyList<Guid> tagged = [];
        if (passengers is not null)
        {
            if (!PostPassenger.AllowedFor(post.Types))
            {
                await passengers.ClearAsync(post, cancellationToken);
            }
            else if (passengerIds is not null)
            {
                tagged = await passengers.ApplyAsync(post, passengerIds, cancellationToken);
            }
        }
        if (request.IsSensitive is { } sensitive)
        {
            post.SetSensitive(sensitive, now);
        }

        post.ApplyScreening(await screener.ScreenAsync(PostAccess.TextOf(request), cancellationToken));
        if (hashtags is not null)
        {
            await hashtags.SyncAsync(post, cancellationToken);
        }

        await posts.SaveChangesAsync(cancellationToken);
        await PassengerTagging.NotifyTaggedAsync(notifications, post, tagged, cancellationToken);

        if (request.IsSensitive is not null)
        {
            // After saving, every time (safe to repeat): sending the edit again retries moving the files if it failed.
            await media.SetSensitiveAsync(post.Media.Select(m => m.MediaId).ToList(), post.IsSensitive, cancellationToken);
        }

        return await views.BuildAsync(post, caller.UserId(), cancellationToken);
    }
}
