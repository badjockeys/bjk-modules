using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Media.UseCases.Review;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Moderation;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class MediaReviewTests
{
    private static readonly Guid ModeratorId = Guid.Parse("4f1c2d3e-0000-4000-8000-0000000000aa");

    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly IModerationAuditLog _audit = Substitute.For<IModerationAuditLog>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public MediaReviewTests()
    {
        _caller.IdentityId.Returns(ModeratorId.ToString());
        _media.SaveChangesWithMessagesAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task>>(0)(ci.ArgAt<CancellationToken>(1)));
        _caller.HasScope(MediaModule.ManageScope).Returns(true);
        _caller.HasRole(StaffRoles.Moderator).Returns(true);
        _caller.UsedMfa.Returns(true);
        _storage.PublicUrl(Arg.Any<string>()).Returns(ci => new Uri("https://media.example.com/" + ci.Arg<string>()));
        _storage.CreateOriginalDownloadUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri("https://originals.example.com/" + ci.ArgAt<string>(0) + "?X-Amz-Signature=abc"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaItem InReview(string contentType = "video/mp4")
    {
        var item = MediaItem.Create(UserId, "clip", contentType, Now);
        item.MarkUploaded(100, Now, TimeSpan.FromDays(90));
        item.RecordRedaction(1, 0);
        item.MarkNeedsReview("The NumberPlate detection model is not configured.", 1234, 10m, 1920, 1080, null, Now);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }

    [Fact]
    public async Task The_queue_lists_media_needing_review_with_signed_preview_links()
    {
        var item = InReview();
        _media.ListByStatusAsync(MediaStatus.NeedsReview, 1, 20, Arg.Any<CancellationToken>()).Returns(((IReadOnlyList<MediaItem>)[item], 1));
        var handler = new ListMediaForReviewQueryHandler(_media, _storage, _caller, _clock);

        var page = await handler.Handle(new ListMediaForReviewQuery(), Ct);

        var dto = Assert.Single(page.Items);
        Assert.Equal(item.Id, dto.Id);
        Assert.Contains("not configured", dto.ReviewReason);
        Assert.Equal($"https://originals.example.com/{UserId:D}/{item.Id:D}/review/720p.mp4?X-Amz-Signature=abc", dto.PreviewUrl!.ToString());
        Assert.Equal(Now + MediaReview.PreviewLifetime, dto.PreviewExpiresAt);
    }

    [Fact]
    public async Task Ordinary_users_cannot_see_the_queue()
    {
        _caller.HasRole(StaffRoles.Moderator).Returns(false);
        var handler = new ListMediaForReviewQueryHandler(_media, _storage, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new ListMediaForReviewQuery(), Ct));
    }

    [Fact]
    public async Task Approving_publishes_the_blurred_files_and_makes_it_ready()
    {
        var item = InReview();
        var handler = new ApproveMediaCommandHandler(_media, _storage, _queue, _audit, _caller, _clock);

        var dto = await handler.Handle(new ApproveMediaCommand(item.Id), Ct);

        Assert.Equal(MediaStatus.Ready, dto.Status);
        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.Equal(ModeratorId, item.ReviewedBy);
        Assert.Equal($"{UserId:D}/{item.Id:D}/720p.mp4", item.PlaybackKey);
        await _storage.Received(1).PublishPrivateAsync(
            $"{UserId:D}/{item.Id:D}/review/720p.mp4", $"{UserId:D}/{item.Id:D}/720p.mp4", "video/mp4", Arg.Any<CancellationToken>());
        await _storage.Received(1).PublishPrivateAsync(
            $"{UserId:D}/{item.Id:D}/review/thumbnail.jpg", $"{UserId:D}/{item.Id:D}/thumbnail.jpg", "image/jpeg", Arg.Any<CancellationToken>());
        await _media.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.ApproveMedia && e.TargetId == item.Id.ToString() && e.After == "Status=Ready"),
            Arg.Any<CancellationToken>());
        var change = Assert.Single(item.TakeStatusChanges());
        Assert.Equal(MediaStatus.Ready, change.Status);
    }

    [Fact]
    public async Task Approving_sensitive_media_keeps_its_files_private_and_queues_the_preview()
    {
        var item = InReview();
        _media.IsSensitiveAsync(item.Id, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new ApproveMediaCommandHandler(_media, _storage, _queue, _audit, _caller, _clock);

        await handler.Handle(new ApproveMediaCommand(item.Id), Ct);

        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.True(item.IsRestricted);
        await _storage.DidNotReceiveWithAnyArgs().PublishPrivateAsync(default!, default!, default!, Ct);
        await _storage.DidNotReceiveWithAnyArgs().UploadPublicAsync(default!, default!, default!, Ct);
        await _storage.Received(1).CopyAsync(
            MediaBucket.Originals, $"{UserId:D}/{item.Id:D}/review/720p.mp4",
            MediaBucket.Sensitive, $"{UserId:D}/{item.Id:D}/sensitive/720p.mp4", "video/mp4", Arg.Any<CancellationToken>());
        await _storage.Received(1).CopyAsync(
            MediaBucket.Originals, $"{UserId:D}/{item.Id:D}/review/thumbnail.jpg",
            MediaBucket.Sensitive, $"{UserId:D}/{item.Id:D}/sensitive/thumbnail.jpg", "image/jpeg", Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(Arg.Is<MediaProcessingJob>(j => j.Step == MediaJobStep.Visibility && j.MediaId == item.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moderators_get_signed_links_to_restricted_media()
    {
        var item = InReview();
        _media.IsSensitiveAsync(item.Id, Arg.Any<CancellationToken>()).Returns(true);
        _storage.CreateDownloadUrlAsync(MediaBucket.Sensitive, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri("https://sensitive.example.com/" + ci.ArgAt<string>(1) + "?X-Amz-Signature=abc"));
        await new ApproveMediaCommandHandler(_media, _storage, _queue, _audit, _caller, _clock).Handle(new ApproveMediaCommand(item.Id), Ct);

        var dto = await new GetMediaForReviewQueryHandler(_media, _storage, _caller, _clock).Handle(new GetMediaForReviewQuery(item.Id), Ct);

        Assert.Equal($"https://sensitive.example.com/{UserId:D}/{item.Id:D}/sensitive/720p.mp4?X-Amz-Signature=abc", dto.PreviewUrl!.ToString());
        Assert.NotNull(dto.PreviewExpiresAt);
    }

    [Fact]
    public async Task Only_media_waiting_for_review_can_be_approved()
    {
        var item = MediaItem.Create(UserId, "clip", "video/mp4", Now);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        var handler = new ApproveMediaCommandHandler(_media, _storage, _queue, _audit, _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ApproveMediaCommand(item.Id), Ct));
        await _storage.DidNotReceiveWithAnyArgs().PublishPrivateAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Rejecting_fails_the_media_with_the_reason_and_removes_the_review_files()
    {
        var item = InReview();
        var handler = new RejectMediaCommandHandler(_media, _storage, _audit, _caller, _clock);

        await handler.Handle(new RejectMediaCommand(item.Id, " A face couldn't be blurred reliably. "), Ct);

        Assert.Equal(MediaStatus.Failed, item.Status);
        Assert.Equal("A face couldn't be blurred reliably.", item.FailureReason);
        await _storage.Received(1).DeleteOriginalAsync($"{UserId:D}/{item.Id:D}/review/720p.mp4", Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().DeleteOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.RejectMedia && e.Reason == "A face couldn't be blurred reliably."),
            Arg.Any<CancellationToken>());
        var change = Assert.Single(item.TakeStatusChanges());
        Assert.Equal((MediaStatus.Failed, "A face couldn't be blurred reliably."), (change.Status, change.Reason));
    }

    [Fact]
    public async Task Only_admins_get_the_unblurred_original()
    {
        var item = InReview();
        var handler = new GetOriginalForReviewQueryHandler(_media, _storage, _audit, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new GetOriginalForReviewQuery(item.Id), Ct));

        _caller.HasRole(StaffRoles.Admin).Returns(true);
        var link = await handler.Handle(new GetOriginalForReviewQuery(item.Id), Ct);
        Assert.Contains(item.OriginalKey, link.Url.ToString());
        await _audit.Received(1).RecordAsync(
            Arg.Is<ModerationAuditEntry>(e => e.Action == ModerationActions.ViewMediaOriginal && e.TargetId == item.Id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_blur_regions_to_ready_media_takes_it_down_and_reprocesses_it()
    {
        var item = MediaItem.Create(UserId, "clip", "video/mp4", Now);
        item.MarkUploaded(100, Now, TimeSpan.FromDays(90));
        item.MarkReady(1234, 10m, 1920, 1080, null, Now);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        var handler = new AddManualRedactionsCommandHandler(_media, _storage, _queue, _audit, _caller, _clock);
        var region = new ManualRedaction(0.1, 0.1, 0.2, 0.1, 2, 4);

        var dto = await handler.Handle(new AddManualRedactionsCommand(item.Id, [region]), Ct);

        Assert.Equal(MediaStatus.Processing, dto.Status);
        Assert.Equal(1, item.Revision);
        Assert.Equal(region, Assert.Single(item.ManualRedactions));
        Assert.Null(item.PlaybackKey);
        await _storage.Received(1).DeletePublicAsync(
            Arg.Is<IEnumerable<string>>(k => k.Contains($"{UserId:D}/{item.Id:D}/720p.mp4")), Arg.Any<CancellationToken>());
        await _queue.Received(1).EnqueueAsync(Arg.Is<MediaProcessingJob>(j => j.MediaId == item.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blur_regions_cannot_be_added_while_still_processing()
    {
        var item = MediaItem.Create(UserId, "clip", "video/mp4", Now);
        item.MarkUploaded(100, Now, TimeSpan.FromDays(90));
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        var handler = new AddManualRedactionsCommandHandler(_media, _storage, _queue, _audit, _caller, _clock);

        await Assert.ThrowsAsync<ValidationException>(
            () => handler.Handle(new AddManualRedactionsCommand(item.Id, [new ManualRedaction(0, 0, 0.5, 0.5)]), Ct));
    }

    [Theory]
    [InlineData(0.1, 0.1, 0.2, 0.2, null, null, true)]
    [InlineData(0.9, 0.1, 0.2, 0.2, null, null, false)]
    [InlineData(-0.1, 0.1, 0.2, 0.2, null, null, false)]
    [InlineData(0.1, 0.1, 0, 0.2, null, null, false)]
    [InlineData(0.1, 0.1, 0.2, 0.2, 5.0, 3.0, false)]
    [InlineData(0.1, 0.1, 0.2, 0.2, 3.0, 5.0, true)]
    public void Blur_regions_must_fit_the_picture_and_have_a_sensible_time_range(
        double x, double y, double width, double height, double? start, double? end, bool valid)
    {
        var result = new AddManualRedactionsCommandValidator().Validate(
            new AddManualRedactionsCommand(Guid.NewGuid(), [new ManualRedaction(x, y, width, height, start, end)]));

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void At_least_one_region_is_needed()
    {
        Assert.False(new AddManualRedactionsCommandValidator().Validate(new AddManualRedactionsCommand(Guid.NewGuid(), [])).IsValid);
    }
}
