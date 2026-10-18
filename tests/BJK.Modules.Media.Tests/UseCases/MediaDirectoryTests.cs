using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Storage;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class MediaDirectoryTests
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly MediaDirectory _directory;

    public MediaDirectoryTests()
    {
        _storage.PublicUrl(Arg.Any<string>()).Returns(ci => new Uri("https://media.example.com/" + ci.Arg<string>()));
        _directory = new MediaDirectory(_media, _storage, new FixedTimeProvider(Now));
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
