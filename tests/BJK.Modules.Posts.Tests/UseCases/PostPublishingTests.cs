using BJK.Modules.Email;
using BJK.Modules.Email.Contracts;
using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases;
using BJK.Modules.Posts.UseCases.Create;
using BJK.Modules.Posts.UseCases.Get;
using BJK.Modules.Posts.UseCases.Moderation;
using BJK.Modules.Posts.UseCases.Publishing;
using BJK.Modules.Posts.UseCases.Safety;
using BJK.Modules.Profiles.Contracts;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Safety;
using BJK.Modules.Shared.Security;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.UseCases;

/// <summary>Post now, publish when ready: posts made while their media are processing, and the media events.</summary>
public sealed class PostPublishingTests
{
    private const string AuthorEmail = "roadhog@example.com";

    private readonly IPostRepository _posts = Substitute.For<IPostRepository>();
    private readonly IProfileDirectory _profiles = Substitute.For<IProfileDirectory>();
    private readonly IMediaDirectory _media = Substitute.For<IMediaDirectory>();
    private readonly IBlockDirectory _blocks = Substitute.For<IBlockDirectory>();
    private readonly IContentScreener _screener = Substitute.For<IContentScreener>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);
    private readonly Dictionary<Guid, MediaSummary> _mediaState = [];
    private readonly List<EmailMessage> _sent = [];
    private readonly PostViews _views;
    private Post? _saved;

    public PostPublishingTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _caller.EmailVerified.Returns(true);
        _caller.Email.Returns(AuthorEmail);
        _profiles.ExistsAsync(UserId, Arg.Any<CancellationToken>()).Returns(true);
        _profiles.GetSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, ProfileSummary> { [UserId] = new(UserId, "roadhog", "Road Hog", null) });
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Where(_mediaState.ContainsKey).Select(id => _mediaState[id]).ToList());
        _posts.GetStatsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, PostStats>());
        _posts.When(p => p.Add(Arg.Any<Post>())).Do(ci => _saved = ci.Arg<Post>());
        _posts.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci => _saved?.Id == ci.Arg<Guid>() ? _saved : null);
        _posts.FindByMediaAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _saved is not null && _saved.Media.Any(m => m.MediaId == ci.Arg<Guid>()) ? _saved : null);
        _screener.ScreenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ScreeningResult.Clean);
        _blocks.GetBlockedIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent.Add(ci.Arg<EmailMessage>());
                return Task.CompletedTask;
            });
        _views = new PostViews(_posts, _profiles, _media, _caller);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Media(MediaType type, MediaStatus status, string? failure = null)
    {
        var id = Guid.NewGuid();
        Set(id, type, status, failure);
        return id;
    }

    private void Set(Guid id, MediaType type, MediaStatus status, string? failure = null)
    {
        var ready = status == MediaStatus.Ready;
        _mediaState[id] = new MediaSummary(
            id,
            UserId,
            type,
            status,
            ready ? new Uri($"https://media.example.com/{id:D}/file") : null,
            ready ? new Uri($"https://media.example.com/{id:D}/thumb") : null,
            type == MediaType.Video ? 25m : null,
            1280,
            720,
            failure);
    }

    /// <summary>Ready media that automatic graphic content detection marked sensitive.</summary>
    private void SetAutoSensitive(Guid id, double score)
    {
        Set(id, MediaType.Video, MediaStatus.Ready);
        _mediaState[id] = _mediaState[id] with { IsAutoSensitive = true, SensitiveScore = score, IsRestricted = true, PlaybackUrl = null, ThumbnailUrl = null };
    }

    private ModerationNotifications Notifications() =>
        new(_email, Options.Create(new EmailOptions()), Options.Create(new PostsOptions { ReportsInbox = "" }), _clock, NullLogger<ModerationNotifications>.Instance);

    private PostMediaPublisher Publisher() => new(_posts, _media, Notifications(), _clock, NullLogger<PostMediaPublisher>.Instance, _audit);

    private CreatePostCommandHandler CreateHandler() => new(
        _posts,
        _profiles,
        _media,
        _screener,
        new NewAccountLimits(_profiles, _posts, Substitute.For<ICommentRepository>(), Options.Create(new SafetyOptions()), _clock),
        _views,
        Publisher(),
        _caller,
        _clock);

    private PostsMediaEventHandler EventHandler() => new(_posts, Publisher());

    private static CreatePostCommand Command(params Guid[] mediaIds)
    {
        var d = Details();
        return new CreatePostCommand(d.Title, d.Description, d.Category, d.IncidentAt, d.Latitude, d.Longitude, d.PlaceName, mediaIds);
    }

    private static MediaEvent Event(Guid mediaId, MediaEventType type, string? reason = null) =>
        new(Guid.NewGuid(), mediaId, UserId, type, reason, Now);

    [Fact]
    public async Task A_post_can_be_made_while_its_video_is_processing_and_waits_unseen()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);

        var dto = await CreateHandler().Handle(Command(video), Ct);

        Assert.Equal(PostMediaState.Pending, dto.MediaState);
        Assert.True(dto.IsProcessing);
        Assert.Equal(PostStatus.Published, dto.Status);
        Assert.Empty(dto.Media);
        Assert.False(_saved!.IsPublic);
        Assert.Equal(AuthorEmail, _saved.NotifyEmail);

        // Others (and logged-out viewers) can't see it; its author can.
        _caller.IdentityId.Returns(OtherUserId.ToString());
        await Assert.ThrowsAsync<NotFoundException>(() => new GetPostQueryHandler(_posts, _views, _blocks, _caller).Handle(new GetPostQuery(_saved.Id), Ct));
        _caller.IdentityId.Returns(UserId.ToString());
        var mine = await new GetPostQueryHandler(_posts, _views, _blocks, _caller).Handle(new GetPostQuery(_saved.Id), Ct);
        Assert.Equal(PostMediaState.Pending, mine.MediaState);
    }

    [Fact]
    public async Task Media_waiting_for_a_moderators_review_can_be_posted_too()
    {
        var video = Media(MediaType.Video, MediaStatus.NeedsReview);

        var dto = await CreateHandler().Handle(Command(video), Ct);

        Assert.Equal(PostMediaState.Pending, dto.MediaState);
    }

    [Fact]
    public async Task The_post_is_published_when_its_media_ready_event_arrives_and_repeats_change_nothing()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);
        await CreateHandler().Handle(Command(video), Ct);
        _posts.ClearReceivedCalls();

        Set(video, MediaType.Video, MediaStatus.Ready);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);

        Assert.Equal(PostMediaState.Ready, _saved!.MediaState);
        Assert.True(_saved.IsPublic);
        Assert.Equal(Now, _saved.MediaReadyAt);
        Assert.Null(_saved.NotifyEmail);
        await _posts.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());

        // The same event again (a queue can deliver twice): nothing more happens.
        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);
        await _posts.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Empty(_sent);

        var dto = await _views.BuildAsync(_saved, UserId, Ct);
        Assert.Single(dto.Media);
        Assert.False(dto.IsProcessing);
    }

    [Fact]
    public async Task Originals_are_released_once_the_post_is_published_and_not_before()
    {
        var ready = Media(MediaType.Image, MediaStatus.Ready);
        await CreateHandler().Handle(Command(ready), Ct);
        await _media.Received(1).ReleaseOriginalsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == ready), Arg.Any<CancellationToken>());

        _media.ClearReceivedCalls();
        var video = Media(MediaType.Video, MediaStatus.NeedsReview);
        await CreateHandler().Handle(Command(video), Ct);
        await _media.DidNotReceiveWithAnyArgs().ReleaseOriginalsAsync(default!, Ct);

        // A moderator approves the video: it becomes Ready, the post is published and its original released.
        Set(video, MediaType.Video, MediaStatus.Ready);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);
        await _media.Received(1).ReleaseOriginalsAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == video), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Media_flagged_by_detection_tags_the_post_sensitive_before_it_is_published()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);
        await CreateHandler().Handle(Command(video), Ct);
        _posts.ClearReceivedCalls();

        SetAutoSensitive(video, 0.82);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);

        Assert.True(_saved!.IsPublic);
        Assert.True(_saved.IsSensitive);
        Assert.Equal(PostSensitiveSource.Automatic, _saved.SensitiveSource);
        Assert.Equal(SensitiveReviewStatus.Pending, _saved.SensitiveReview);
        Assert.Equal(0.82, _saved.SensitiveScore);
        // Tagged and published in one save: it's never public untagged.
        await _posts.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.AutoTagSensitive && e.SystemActor == ModerationActor.AutomaticRole && e.TargetId == _saved.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _media.Received(1).SetSensitiveAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(video)), true, Arg.Any<CancellationToken>(), Arg.Any<bool>());

        // Others see it blurred.
        _caller.IdentityId.Returns(OtherUserId.ToString());
        var seen = await new GetPostQueryHandler(_posts, _views, _blocks, _caller).Handle(new GetPostQuery(_saved.Id), Ct);
        Assert.True(seen.RequiresAgeVerification);
        Assert.True(Assert.Single(seen.Media).IsBlurred);
        Assert.Equal(PostSensitiveSource.Automatic, seen.SensitiveSource);
    }

    [Fact]
    public async Task Posting_media_already_flagged_by_detection_tags_the_post_whatever_the_author_ticked()
    {
        var video = Media(MediaType.Video, MediaStatus.Ready);
        SetAutoSensitive(video, 0.5);
        var markedBeforeTheSave = false;
        _media.When(m => m.SetSensitiveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), true, Arg.Any<CancellationToken>(), Arg.Any<bool>()))
            .Do(_ => markedBeforeTheSave |= _saved is null);

        var dto = await CreateHandler().Handle(Command(video), Ct);

        Assert.True(dto.IsSensitive);
        Assert.Equal(PostSensitiveSource.Automatic, dto.SensitiveSource);
        Assert.Equal(SensitiveReviewStatus.Pending, _saved!.SensitiveReview);
        Assert.True(markedBeforeTheSave);
        await _audit.Received(1).RecordAsync(Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.AutoTagSensitive), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_published_post_is_tagged_if_its_media_is_flagged_when_processed_again()
    {
        var video = Media(MediaType.Video, MediaStatus.Ready);
        await CreateHandler().Handle(Command(video), Ct);
        Assert.False(_saved!.IsSensitive);

        SetAutoSensitive(video, 0.9);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);

        Assert.True(_saved.IsSensitive);
        Assert.Equal(SensitiveReviewStatus.Pending, _saved.SensitiveReview);
    }

    [Fact]
    public async Task Detection_does_not_tag_a_post_again_after_a_moderator_undid_it()
    {
        var video = Media(MediaType.Video, MediaStatus.Ready);
        SetAutoSensitive(video, 0.5);
        await CreateHandler().Handle(Command(video), Ct);
        _saved!.ReviewAutomaticSensitive(confirm: false, Guid.NewGuid(), Now);

        await EventHandler().HandleAsync(Event(video, MediaEventType.Ready), Ct);

        Assert.False(_saved.IsSensitive);
        Assert.Equal(SensitiveReviewStatus.Undone, _saved.SensitiveReview);
    }

    [Fact]
    public async Task A_photo_post_waits_until_every_photo_is_ready()
    {
        var first = Media(MediaType.Image, MediaStatus.Ready);
        var second = Media(MediaType.Image, MediaStatus.Processing);
        var third = Media(MediaType.Image, MediaStatus.Processing);
        await CreateHandler().Handle(Command(first, second, third), Ct);

        Set(second, MediaType.Image, MediaStatus.Ready);
        await EventHandler().HandleAsync(Event(second, MediaEventType.Ready), Ct);
        Assert.Equal(PostMediaState.Pending, _saved!.MediaState);

        Set(third, MediaType.Image, MediaStatus.Ready);
        await EventHandler().HandleAsync(Event(third, MediaEventType.Ready), Ct);
        Assert.Equal(PostMediaState.Ready, _saved.MediaState);
    }

    [Fact]
    public async Task If_the_video_fails_the_post_stays_unpublished_and_the_author_is_emailed_once()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);
        await CreateHandler().Handle(Command(video), Ct);

        const string reason = "A moderator couldn't confirm every face was blurred.";
        Set(video, MediaType.Video, MediaStatus.Failed, reason);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Failed, reason), Ct);
        await EventHandler().HandleAsync(Event(video, MediaEventType.Failed, reason), Ct);

        Assert.Equal(PostMediaState.Failed, _saved!.MediaState);
        Assert.Equal(reason, _saved.MediaFailureReason);
        Assert.False(_saved.IsPublic);
        Assert.Null(_saved.NotifyEmail);
        var email = Assert.Single(_sent);
        Assert.Equal(AuthorEmail, email.To);
        Assert.Equal("Your Bad Jockeys post couldn't be published", email.Subject);
        Assert.Contains(reason, email.TextBody);
        Assert.Contains("Overtook on a blind bend", email.TextBody);

        var dto = await _views.BuildAsync(_saved, UserId, Ct);
        Assert.Equal(PostMediaState.Failed, dto.MediaState);
        Assert.Equal(reason, dto.MediaFailureReason);
    }

    [Fact]
    public async Task Deleting_a_photo_before_it_is_ready_fails_the_post()
    {
        var first = Media(MediaType.Image, MediaStatus.Processing);
        var second = Media(MediaType.Image, MediaStatus.Processing);
        await CreateHandler().Handle(Command(first, second), Ct);

        _mediaState.Remove(second);
        await EventHandler().HandleAsync(Event(second, MediaEventType.Failed, "deleted"), Ct);

        Assert.Equal(PostMediaState.Failed, _saved!.MediaState);
        Assert.Contains("deleted", _saved.MediaFailureReason);
    }

    [Fact]
    public async Task Media_that_finishes_while_the_post_is_being_saved_still_publishes_it()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);

        // The video becomes Ready just after the handler's first look, before its event could find the post.
        var looks = 0;
        _media.GetAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++looks == 2)
            {
                Set(video, MediaType.Video, MediaStatus.Ready);
            }

            return new List<MediaSummary> { _mediaState[video] };
        });

        var dto = await CreateHandler().Handle(Command(video), Ct);

        Assert.Equal(PostMediaState.Ready, _saved!.MediaState);
        Assert.Equal(PostMediaState.Ready, dto.MediaState);
    }

    [Fact]
    public async Task Posts_with_ready_media_are_published_straight_away_without_keeping_the_email()
    {
        var video = Media(MediaType.Video, MediaStatus.Ready);

        var dto = await CreateHandler().Handle(Command(video), Ct);

        Assert.Equal(PostMediaState.Ready, dto.MediaState);
        Assert.True(_saved!.IsPublic);
        Assert.Null(_saved.NotifyEmail);
        Assert.Null(_saved.MediaReadyAt);
    }

    [Fact]
    public async Task A_post_has_photos_or_one_video_not_both()
    {
        var video = Media(MediaType.Video, MediaStatus.Processing);
        var photo = Media(MediaType.Image, MediaStatus.Ready);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().Handle(Command(photo, video), Ct));

        Assert.Equal(CreatePostCommandHandler.PhotosOrVideoMessage, ex.Errors.Single().ErrorMessage);

        var secondVideo = Media(MediaType.Video, MediaStatus.Ready);
        await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().Handle(Command(video, secondVideo), Ct));
        _posts.DidNotReceive().Add(Arg.Any<Post>());
    }

    [Fact]
    public async Task Events_for_media_not_in_a_post_are_ignored()
    {
        await EventHandler().HandleAsync(Event(Guid.NewGuid(), MediaEventType.Ready), Ct);

        await _posts.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_deleted_or_already_published_post_ignores_media_changes()
    {
        var post = Post.Create(UserId, Details(), [Guid.NewGuid()], Now, mediaPending: true, notifyEmail: AuthorEmail);
        Assert.True(post.MarkMediaReady(Now));
        Assert.False(post.MarkMediaReady(Now));
        Assert.False(post.MarkMediaFailed("too late", Now));

        var deleted = Post.Create(UserId, Details(), [Guid.NewGuid()], Now, mediaPending: true);
        deleted.Delete(Now);
        Assert.False(deleted.MarkMediaReady(Now));
    }
}
