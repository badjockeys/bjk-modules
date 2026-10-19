using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class MediaDirectoryTests
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly MediaDirectory _directory;

    public MediaDirectoryTests()
    {
        _storage.PublicUrl(Arg.Any<string>()).Returns(ci => new Uri("https://media.example.com/" + ci.Arg<string>()));
        _storage.CreateDownloadUrlAsync(MediaBucket.Sensitive, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Uri("https://account.r2.cloudflarestorage.com/sensitive/" + ci.ArgAt<string>(1) + "?X-Amz-Signature=abc"));
        _media.GetSensitiveMarksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, SensitiveMediaMark>());
        _directory = new MediaDirectory(_media, _storage, _queue, Options(), new FixedTimeProvider(Now), NullLogger<MediaDirectory>.Instance);
    }

    private static MediaItem Ready(string contentType = "video/mp4")
    {
        var item = MediaItem.Create(UserId, "clip", contentType, Now);
        item.MarkUploaded(10, Now, TimeSpan.FromDays(90));
        item.MarkReady(5, 3m, 1280, 720, null, Now);
        return item;
    }

    [Fact]
    public async Task Restricted_media_has_only_its_blurred_preview_unless_signed_links_are_asked_for()
    {
        var item = Ready();
        item.Restrict(MediaKeys.Preview(UserId, item.Id), Now);
        _media.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaItem> { item });

        var plain = Assert.Single(await _directory.GetAsync([item.Id], Ct));
        var signed = Assert.Single(await _directory.GetWithSensitiveLinksAsync([item.Id], Ct));

        Assert.True(plain.IsRestricted);
        Assert.Null(plain.PlaybackUrl);
        Assert.Null(plain.ThumbnailUrl);
        Assert.Equal($"https://media.example.com/{UserId:D}/{item.Id:D}/preview.webp", plain.PreviewUrl!.ToString());
        Assert.StartsWith($"https://account.r2.cloudflarestorage.com/sensitive/{UserId:D}/{item.Id:D}/sensitive/720p.mp4", signed.PlaybackUrl!.ToString());
        Assert.Contains("/sensitive/thumbnail.jpg", signed.ThumbnailUrl!.ToString());
        Assert.Equal(Now.AddMinutes(10), signed.LinksExpireAt);
        await _storage.Received(1).CreateDownloadUrlAsync(MediaBucket.Sensitive, Arg.Any<string>(), TimeSpan.FromMinutes(10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Public_media_keeps_its_public_addresses_with_sensitive_links()
    {
        var item = Ready();
        _media.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaItem> { item });

        var signed = Assert.Single(await _directory.GetWithSensitiveLinksAsync([item.Id], Ct));

        Assert.Equal($"https://media.example.com/{UserId:D}/{item.Id:D}/720p.mp4", signed.PlaybackUrl!.ToString());
        Assert.Null(signed.LinksExpireAt);
        await _storage.DidNotReceiveWithAnyArgs().CreateDownloadUrlAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task Marking_ready_media_sensitive_queues_moving_its_files()
    {
        var item = Ready();
        _media.GetUntrackedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaItem> { item });
        _media.SetSensitiveAsync(item.Id, true, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<SensitiveMarkSource>(), Arg.Any<double?>(), Arg.Any<bool>())
            .Returns(true);
        _media.GetSensitiveMarksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, SensitiveMediaMark> { [item.Id] = new(item.Id, Now) });

        await _directory.SetSensitiveAsync([item.Id], true, Ct);

        await _queue.Received(1).EnqueueAsync(
            Arg.Is<MediaProcessingJob>(j => j.MediaId == item.Id && j.Step == MediaJobStep.Visibility && j.RequestId != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeating_a_mark_whose_files_already_match_queues_nothing()
    {
        var item = Ready();
        item.Restrict(MediaKeys.Preview(UserId, item.Id), Now);
        _media.GetUntrackedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaItem> { item });
        _media.GetSensitiveMarksAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, SensitiveMediaMark> { [item.Id] = new(item.Id, Now) });

        await _directory.SetSensitiveAsync([item.Id], true, Ct);

        await _queue.DidNotReceiveWithAnyArgs().EnqueueAsync(default!, Ct);
    }

    [Fact]
    public async Task Clearing_leaves_automatic_marks_unless_a_moderator_clears_them()
    {
        var item = Ready();
        _media.GetUntrackedAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<MediaItem> { item });

        await _directory.SetSensitiveAsync([item.Id], false, Ct);
        await _directory.SetSensitiveAsync([item.Id], false, Ct, clearAutomatic: true);

        Received.InOrder(() =>
        {
            _media.SetSensitiveAsync(item.Id, false, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<SensitiveMarkSource>(), Arg.Any<double?>(), false);
            _media.SetSensitiveAsync(item.Id, false, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<SensitiveMarkSource>(), Arg.Any<double?>(), true);
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Urls_are_only_given_for_ready_media()
    {
        var ready = MediaItem.Create(UserId, "a.mp4", "video/mp4", Now);
        ready.MarkUploaded(10, Now, TimeSpan.FromDays(90));
        ready.MarkReady(5, 3m, 1280, 720, null, Now);
        var processing = MediaItem.Create(UserId, "b.mp4", "video/mp4", Now);
        processing.MarkUploaded(10, Now, TimeSpan.FromDays(90));
        _media.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaItem> { ready, processing });

        var result = await _directory.GetAsync([ready.Id, processing.Id], Ct);

        Assert.NotNull(result.Single(m => m.Id == ready.Id).PlaybackUrl);
        Assert.Null(result.Single(m => m.Id == processing.Id).PlaybackUrl);
        Assert.False(result.Single(m => m.Id == processing.Id).IsReady);
    }

    [Fact]
    public async Task Delete_removes_files_and_marks_media_deleted()
    {
        var item = MediaItem.Create(UserId, "a.jpg", "image/jpeg", Now);
        _media.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MediaItem> { item });

        await _directory.DeleteAsync([item.Id], Ct);

        await _storage.Received(1).DeleteOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>());
        Assert.Equal(MediaStatus.Deleted, item.Status);
        await _media.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

public sealed class MediaLifecycleTests
{
    [Fact]
    public async Task Deleting_the_account_removes_all_files_then_all_rows()
    {
        var media = Substitute.For<IMediaRepository>();
        var storage = Substitute.For<IMediaStorage>();
        var handler = new MediaUserLifecycleHandler(media, storage);
        var ct = TestContext.Current.CancellationToken;

        await handler.HandleAsync(new BJK.Modules.Shared.Users.UserLifecycleEvent(Guid.NewGuid(), UserId, BJK.Modules.Shared.Users.UserLifecycleEventType.Deleted, Now), ct);

        Received.InOrder(() =>
        {
            storage.DeleteAllForUserAsync(UserId, Arg.Any<CancellationToken>());
            media.DeleteAllForUserAsync(UserId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Deactivation_leaves_media_alone()
    {
        var media = Substitute.For<IMediaRepository>();
        var storage = Substitute.For<IMediaStorage>();
        var handler = new MediaUserLifecycleHandler(media, storage);

        await handler.HandleAsync(new BJK.Modules.Shared.Users.UserLifecycleEvent(Guid.NewGuid(), UserId, BJK.Modules.Shared.Users.UserLifecycleEventType.Deactivated, Now), TestContext.Current.CancellationToken);

        await storage.DidNotReceive().DeleteAllForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
