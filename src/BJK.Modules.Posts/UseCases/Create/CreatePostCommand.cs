using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Passengers;
using BJK.Modules.Posts.UseCases.Publishing;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Safety;
using BJK.Modules.Shared.Security;
using BJK.Modules.Shared.Notifications;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BJK.Modules.Posts.UseCases.Create;

/// <summary>
/// Creates a post with 1 to 10 photos or one video, uploaded through <c>/api/media</c> first. They don't have to have
/// finished processing: if any is still Processing (or waiting for a moderator's review), the post is saved as
/// <see cref="PostMediaState.Pending"/>, seen only by its author (in "my posts") and moderators, and is published
/// automatically once they're all Ready, or marked Failed (and the author emailed) if one fails. If they're all Ready
/// already, it's visible to everyone straight away. Either way content screening can hold it for review. The author
/// needs a confirmed email and a profile (and a passed age check if <c>Safety:RequireAgeVerification</c> is on).
/// <c>IsSensitive</c> marks graphic content: the post shows to everyone, its media blurred except for age-verified
/// adults, and the media are kept privately. If automatic detection already flagged any of the media, the post is
/// tagged sensitive automatically (and queued for a moderator) whatever <c>IsSensitive</c> says. <c>Types</c> are the
/// types of bad driving (at least one; the first is the main one); older clients can send one <c>Category</c> instead.
/// <c>Passengers</c> are @handles of people in the author's vehicle (driving incidents only, up to 4, adults only):
/// each is asked to accept before their name shows on the post. <c>DraftId</c> publishes one of the author's drafts:
/// the draft is deleted once the post is saved (its photos and videos stay, in the post).
/// </summary>
public sealed record CreatePostCommand(
    string Title,
    string? Description,
    IncidentCategory? Category,
    DateTimeOffset IncidentAt,
    decimal Latitude,
    decimal Longitude,
    string? PlaceName,
    IReadOnlyList<Guid> MediaIds,
    bool IsSensitive = false,
    IReadOnlyList<IncidentCategory>? Types = null,
    IReadOnlyList<string>? Passengers = null,
    VehicleType? VehicleType = null,
    string? VehicleMake = null,
    string? VehicleModel = null,
    VehicleColour? VehicleColour = null,
    bool? ReportedToPolice = null,
    Guid? DraftId = null) : IRequest<PostDto>, IPostDetails, IRequiresAgeVerification;

public sealed class CreatePostCommandValidator : PostDetailsValidator<CreatePostCommand>
{
    public CreatePostCommandValidator(TimeProvider clock, IOptions<PostsOptions> options)
        : base(clock, options)
    {
        RuleFor(c => c.MediaIds)
            .NotEmpty()
            .WithMessage("Add at least one photo or video.")
            .Must(ids => ids is null || ids.Count <= Post.MaxMedia)
            .WithMessage($"A post can have up to {Post.MaxMedia} photos or videos.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Each photo or video can only be added once.")
            .Must(ids => ids is null || !ids.Contains(Guid.Empty))
            .WithMessage("Media ids can't be empty.");
    }
}

public sealed class CreatePostCommandHandler(
    IPostRepository posts,
    IProfileDirectory profiles,
    IMediaDirectory media,
    IContentScreener screener,
    NewAccountLimits limits,
    PostViews views,
    PostMediaPublisher publisher,
    ICurrentUser caller,
    TimeProvider clock,
    PassengerTagging? passengers = null,
    INotificationSender? notifications = null,
    IAreaAlerts? areaAlerts = null,
    IDraftRepository? drafts = null,
    Hashtags.HashtagSync? hashtags = null,
    IEngagementRepository? engagement = null)
    : IRequestHandler<CreatePostCommand, PostDto>
{
    /// <summary>The message when a post mixes a video with anything else.</summary>
    public const string PhotosOrVideoMessage = "A post can have 1 to 10 photos or one video, not both. Post the video on its own.";

    public async Task<PostDto> Handle(CreatePostCommand request, CancellationToken cancellationToken)
    {
        var userId = caller.RequireUserId();
        PostAccess.RequireVerifiedEmail(caller, "posting");

        if (!await profiles.ExistsAsync(userId, cancellationToken))
        {
            throw PostAccess.Invalid("Profile", "Create your profile before posting.");
        }

        await limits.EnsureCanPostAsync(userId, cancellationToken);

        // Publishing a draft: it must be the caller's own. It's removed in the same save as the post.
        var draft = request.DraftId is { } draftId && drafts is not null
            ? await Drafts.SaveDraftCommandHandler.GetOwnAsync(drafts, caller, draftId, cancellationToken)
            : null;

        var found = (await media.GetAsync(request.MediaIds, cancellationToken)).ToDictionary(m => m.Id);
        foreach (var id in request.MediaIds)
        {
            if (!found.TryGetValue(id, out var item) || item.UserId != userId)
            {
                throw PostAccess.Invalid(nameof(request.MediaIds), $"Media {id} isn't one of your uploads.");
            }

            switch (item.Status)
            {
                case MediaStatus.Ready or MediaStatus.Processing or MediaStatus.NeedsReview:
                    break;
                case MediaStatus.Uploading:
                    throw PostAccess.Invalid(
                        nameof(request.MediaIds),
                        $"Media {id} hasn't finished uploading. Finish the upload, then post again.");
                case MediaStatus.Failed:
                    throw PostAccess.Invalid(
                        nameof(request.MediaIds),
                        $"Media {id} couldn't be processed: {item.FailureReason ?? "try uploading it again."} Remove it from the post.");
                default:
                    throw PostAccess.Invalid(nameof(request.MediaIds), $"Media {id} isn't one of your uploads.");
            }
        }

        // 1 to 10 photos, or one video on its own.
        if (request.MediaIds.Count > 1 && request.MediaIds.Any(id => found[id].MediaType == MediaType.Video))
        {
            throw PostAccess.Invalid(nameof(request.MediaIds), PhotosOrVideoMessage);
        }

        if (await posts.AnyUseMediaAsync(request.MediaIds, cancellationToken))
        {
            throw PostAccess.Invalid(nameof(request.MediaIds), "A photo or video is already in another post.");
        }

        var details = request.ToDetails();
        var passengerIds = passengers is null || request.Passengers is not { Count: > 0 }
            ? []
            : await passengers.ResolveAsync(userId, request.Passengers, details.Types!, cancellationToken);

        var pending = request.MediaIds.Any(id => !found[id].IsReady);
        var post = Post.Create(
            userId,
            details,
            request.MediaIds,
            clock.GetUtcNow(),
            request.IsSensitive,
            mediaPending: pending,
            notifyEmail: pending ? caller.Email : null,
            hasVideo: request.MediaIds.Any(id => found[id].MediaType == MediaType.Video));
        post.ApplyScreening(await screener.ScreenAsync(PostAccess.TextOf(request), cancellationToken));
        var autoTagged = publisher.TagIfDetected(post, request.MediaIds.Select(id => found[id]));

        // Mark the media before saving the post, so media still processing is kept private when it's ready. Only ever
        // marks: clearing could undo an automatic mark the worker is making at this moment.
        if (post.IsSensitive)
        {
            await media.SetSensitiveAsync(request.MediaIds, isSensitive: true, cancellationToken);
        }

        // The vote closes when the author's setting says (7 days unless they changed it), and they follow their post.
        var preferences = engagement is null ? null : await engagement.GetPreferencesAsync(userId, cancellationToken);
        post.SetVoteDuration(preferences?.VoteDuration ?? VoteDuration.Days7);

        posts.Add(post);
        engagement?.AddFollow(PostFollow.Create(post.Id, userId, post.CreatedAt));
        if (hashtags is not null)
        {
            await hashtags.SyncAsync(post, cancellationToken);
        }

        if (draft is not null)
        {
            drafts!.Remove(draft);
        }

        IReadOnlyList<Guid> tagged = [];
        if (passengers is not null && passengerIds.Count > 0)
        {
            tagged = await passengers.ApplyAsync(post, passengerIds, cancellationToken);
        }

        await posts.SaveChangesAsync(cancellationToken);
        await PassengerTagging.NotifyTaggedAsync(notifications, post, tagged, cancellationToken);
        if (post.IsPublic && areaAlerts is not null)
        {
            await areaAlerts.PostPublishedAsync(post.ToPublished(clock.GetUtcNow()), cancellationToken);
        }
        if (autoTagged)
        {
            await publisher.RecordAutomaticTagAsync(post, cancellationToken);
        }

        if (!pending)
        {
            // Published with its media ready: the uploaded originals aren't needed any more.
            await publisher.ReleaseOriginalsAsync(post, cancellationToken);
        }

        if (pending)
        {
            // A photo or video may have finished between the check above and the save, before its event could find
            // this post, so look again now that the post is saved.
            try
            {
                await publisher.RefreshAsync(post, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The media event handler updated the post at the same moment; it has done the same thing.
            }
        }

        return await views.BuildAsync(post, userId, cancellationToken);
    }
}
