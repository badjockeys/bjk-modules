using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Storage;
using BJK.Modules.Media.UseCases.Delete;
using BJK.Modules.Media.UseCases.Get;
using BJK.Modules.Media.UseCases.Upload;
using BJK.Modules.Shared.Exceptions;
using BJK.Modules.Shared.Security;
using FluentValidation;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.UseCases;

public sealed class MediaHandlerTests
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly ICurrentUser _caller = Substitute.For<ICurrentUser>();
    private readonly TimeProvider _clock = new FixedTimeProvider(Now);

    public MediaHandlerTests()
    {
        _caller.IdentityId.Returns(UserId.ToString());
        _storage.PublicUrl(Arg.Any<string>()).Returns(ci => new Uri("https://media.example.com/" + ci.Arg<string>()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequestUpload_records_media_and_returns_signed_link_for_its_key()
    {
        _storage.CreateOriginalUploadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => Upload(ci.ArgAt<string>(0)));
        var handler = new RequestUploadCommandHandler(_media, _storage, _caller, Options(), _clock);

        var result = await handler.Handle(new RequestUploadCommand("clip.MOV", "video/quicktime", 50_000_000), Ct);

        var expectedKey = $"{UserId:D}/{result.MediaId:D}/original.mov";
        await _storage.Received(1).CreateOriginalUploadAsync(expectedKey, "video/quicktime", TimeSpan.FromMinutes(15), Arg.Any<CancellationToken>());
        _media.Received(1).Add(Arg.Is<MediaItem>(m => m.Id == result.MediaId && m.UserId == UserId));
        Assert.Equal("PUT", result.Upload.Method);
    }

    [Fact]
    public async Task RequestUpload_needs_a_user_token()
    {
        _caller.IdentityId.Returns((string?)null);
        var handler = new RequestUploadCommandHandler(_media, _storage, _caller, Options(), _clock);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => handler.Handle(new RequestUploadCommand("clip.mp4", "video/mp4", 1), Ct));
    }

    [Fact]
    public async Task Complete_queues_processing_when_file_has_arrived()
    {
        var item = Existing(UserId);
        _storage.FindOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>()).Returns(new StoredFile(40_000_000, "video/mp4"));
        var handler = CompleteHandler();

        var dto = await handler.Handle(new CompleteUploadCommand(item.Id), Ct);

        Assert.Equal(MediaStatus.Processing, dto.Status);
        await _queue.Received(1).EnqueueAsync(
            Arg.Is<MediaProcessingJob>(j => j.MediaId == item.Id && j.OriginalKey == item.OriginalKey && j.MediaType == MediaType.Video),
            Arg.Any<CancellationToken>());
        await _media.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_before_the_file_arrives_is_a_validation_error()
    {
        var item = Existing(UserId);
        var handler = CompleteHandler();

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new CompleteUploadCommand(item.Id), Ct));

        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
        Assert.Equal(MediaStatus.Uploading, item.Status);
    }

    [Fact]
    public async Task Complete_rejects_and_deletes_an_oversized_file()
    {
        var item = Existing(UserId);
        _storage.FindOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>()).Returns(new StoredFile(600L * 1024 * 1024, "video/mp4"));
        var handler = CompleteHandler();

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new CompleteUploadCommand(item.Id), Ct));

        await _storage.Received(1).DeleteOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>());
        Assert.Equal(MediaStatus.Failed, item.Status);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_by_someone_else_is_forbidden()
    {
        var item = Existing(OtherUserId);
        var handler = CompleteHandler();

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new CompleteUploadCommand(item.Id), Ct));
    }

    [Fact]
    public async Task Complete_twice_does_not_queue_twice()
    {
        var item = Existing(UserId);
        item.MarkUploaded(1, Now, TimeSpan.FromDays(90));
        var handler = CompleteHandler();

        await handler.Handle(new CompleteUploadCommand(item.Id), Ct);

        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<MediaProcessingJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_hides_unprocessed_media_from_other_users()
    {
        var item = Existing(OtherUserId);
        var handler = new GetMediaQueryHandler(_media, _storage, _caller);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new GetMediaQuery(item.Id), Ct));
    }

    [Fact]
    public async Task Get_shows_ready_media_to_everyone_with_public_urls()
    {
        var item = Existing(OtherUserId);
        item.MarkUploaded(1, Now, TimeSpan.FromDays(90));
        item.MarkReady(10, 30m, 1280, 720, null, Now);
        var handler = new GetMediaQueryHandler(_media, _storage, _caller);

        var dto = await handler.Handle(new GetMediaQuery(item.Id), Ct);

        Assert.Equal($"https://media.example.com/{OtherUserId:D}/{item.Id:D}/720p.mp4", dto.PlaybackUrl!.ToString());
    }

    [Fact]
    public async Task Delete_removes_original_and_published_files()
    {
        var item = Existing(UserId);
        item.MarkUploaded(1, Now, TimeSpan.FromDays(90));
        item.MarkReady(10, 30m, 1280, 720, null, Now);
        var handler = new DeleteMediaCommandHandler(_media, _storage, _caller, _clock);

        await handler.Handle(new DeleteMediaCommand(item.Id), Ct);

        await _storage.Received(1).DeleteOriginalAsync(item.OriginalKey, Arg.Any<CancellationToken>());
        await _storage.Received(1).DeletePublicAsync(
            Arg.Is<IEnumerable<string>>(keys => keys.Count() == 2),
            Arg.Any<CancellationToken>());
        Assert.Equal(MediaStatus.Deleted, item.Status);
    }

    [Fact]
    public async Task Moderator_can_delete_anyones_media_on_the_admin_endpoint()
    {
        var item = Existing(OtherUserId);
        _caller.HasScope(MediaModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        _caller.UsedMfa.Returns(true);
        var handler = new DeleteMediaCommandHandler(_media, _storage, _caller, _clock);

        await handler.Handle(new DeleteMediaCommand(item.Id, AsModerator: true), Ct);

        Assert.Equal(MediaStatus.Deleted, item.Status);
    }

    [Fact]
    public async Task Moderator_without_two_factor_sign_in_is_refused()
    {
        var item = Existing(OtherUserId);
        _caller.HasScope(MediaModule.ManageScope).Returns(true);
        _caller.HasRole("Moderator").Returns(true);
        var handler = new DeleteMediaCommandHandler(_media, _storage, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeleteMediaCommand(item.Id, AsModerator: true), Ct));
        Assert.NotEqual(MediaStatus.Deleted, item.Status);
    }

    [Fact]
    public async Task Staff_rights_dont_apply_on_the_public_endpoint()
    {
        var item = Existing(OtherUserId);
        _caller.HasScope(MediaModule.ManageScope).Returns(true);
        _caller.HasRole("Admin").Returns(true);
        _caller.UsedMfa.Returns(true);
        var handler = new DeleteMediaCommandHandler(_media, _storage, _caller, _clock);

        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new DeleteMediaCommand(item.Id), Ct));
    }

    private CompleteUploadCommandHandler CompleteHandler() =>
        new(_media, _storage, _queue, _caller, Options(), _clock);

    private MediaItem Existing(Guid ownerId)
    {
        var item = MediaItem.Create(ownerId, "clip.mp4", "video/mp4", Now);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }
}
