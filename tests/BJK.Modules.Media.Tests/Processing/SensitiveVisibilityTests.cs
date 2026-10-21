using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using BJK.Modules.Media.Infrastructure.Messaging;
using BJK.Modules.Media.Infrastructure.Processing;
using BJK.Modules.Media.Infrastructure.Processing.Redaction;
using BJK.Modules.Media.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Processing;

/// <summary>
/// Sensitive media in the worker: published privately with a blurred preview, files moved when a post is tagged or
/// untagged (idempotent, restart-safe), and automatic graphic content detection marking media before it's published.
/// Storage is an in-memory stand-in for the three buckets.
/// </summary>
public sealed class SensitiveVisibilityTests
{
    private readonly IMediaRepository _media = Substitute.For<IMediaRepository>();
    private readonly IMediaTranscoder _transcoder = Substitute.For<IMediaTranscoder>();
    private readonly IMediaRedactor _redactor = Substitute.For<IMediaRedactor>();
    private readonly IMediaJobQueue _queue = Substitute.For<IMediaJobQueue>();
    private readonly IMediaCachePurger _purger = Substitute.For<IMediaCachePurger>();
    private readonly MemoryStorage _storage = new();
    private bool _sensitive;
    private RedactionResult _redaction = new(0, 0, [], 300, 60, TimeSpan.FromSeconds(1));

    public SensitiveVisibilityTests()
    {
        _media.IsSensitiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => _sensitive);
        _media.SetSensitiveAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<SensitiveMarkSource>(), Arg.Any<double?>(), Arg.Any<bool>())
            .Returns(ci =>
            {
                var changed = _sensitive != ci.ArgAt<bool>(1);
                _sensitive = ci.ArgAt<bool>(1);
                return changed;
            });
        _transcoder.CreateSensitivePreviewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(1), new byte[7]);
                return Task.CompletedTask;
            });
        _transcoder.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProbeResult(true, 10m, 1280, 720));
        _transcoder.CreateVideoThumbnailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(1), new byte[56]);
                return Task.CompletedTask;
            });
        _redactor.RedactVideoAsync(Arg.Any<string>(), Arg.Any<ProbeResult>(), Arg.Any<IReadOnlyList<ManualRedaction>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(3), new byte[1234]);
                return Task.FromResult(_redaction);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MediaProcessor Processor(SensitiveDetectionOptions? detection = null) => new(
        _media,
        new InMemoryVideoJobStore(),
        _queue,
        _storage,
        _transcoder,
        _redactor,
        Microsoft.Extensions.Options.Options.Create(new MediaOptions()),
        Microsoft.Extensions.Options.Options.Create(new MediaProcessingOptions { WorkingDirectory = Path.GetTempPath(), SegmentSeconds = 0 }),
        Microsoft.Extensions.Options.Options.Create(new MediaRedactionOptions()),
        new FixedTimeProvider(Now),
        NullLogger<MediaProcessor>.Instance,
        _purger,
        Microsoft.Extensions.Options.Options.Create(detection ?? new SensitiveDetectionOptions()));

    private MediaItem Processing()
    {
        var item = MediaItem.Create(UserId, "clip", "video/mp4", Now);
        item.MarkUploaded(10, Now, TimeSpan.FromDays(90));
        _storage.Put(MediaBucket.Originals, item.OriginalKey);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        return item;
    }

    /// <summary>Ready media published normally: its files are in the public bucket.</summary>
    private MediaItem PublicReady()
    {
        var item = Processing();
        item.MarkReady(1234, 10m, 1280, 720, null, Now);
        _storage.Put(MediaBucket.Public, item.PlaybackKey!);
        _storage.Put(MediaBucket.Public, item.ThumbnailKey!);
        return item;
    }

    private static MediaProcessingJob Job(MediaItem item) =>
        new(item.Id, item.UserId, item.MediaType, item.OriginalKey, item.OriginalContentType);

    private static string Folder(MediaItem item) => $"{UserId:D}/{item.Id:D}";

    // Publishing media that's already marked sensitive

    [Fact]
    public async Task Sensitive_media_is_published_privately_with_only_a_blurred_preview_public()
    {
        var item = Processing();
        _sensitive = true;

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.True(item.IsRestricted);
        Assert.Equal($"{Folder(item)}/preview.webp", item.PreviewKey);
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
        Assert.Equal(new[] { $"{Folder(item)}/sensitive/720p.mp4", $"{Folder(item)}/sensitive/thumbnail.jpg" }, _storage.Keys(MediaBucket.Sensitive));
        await _transcoder.Received(1).CreateSensitivePreviewAsync(Arg.Is<string>(p => p.EndsWith("thumbnail.jpg")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Media_that_is_not_sensitive_is_published_as_before()
    {
        var item = Processing();

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.False(item.IsRestricted);
        Assert.Null(item.PreviewKey);
        Assert.Equal(new[] { $"{Folder(item)}/720p.mp4", $"{Folder(item)}/thumbnail.jpg" }, _storage.Keys(MediaBucket.Public));
        Assert.Empty(_storage.Keys(MediaBucket.Sensitive));
    }

    [Fact]
    public async Task If_the_post_is_tagged_while_the_media_processes_its_files_are_moved_straight_after()
    {
        var item = Processing();
        _media.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // The post is tagged just as the worker saves the media as Ready.
            if (item.Status == MediaStatus.Ready)
            {
                _sensitive = true;
            }

            return Task.CompletedTask;
        });

        await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.True(item.IsRestricted);
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
    }

    // Automatic graphic content detection

    [Fact]
    public async Task Media_scoring_over_the_threshold_is_marked_sensitive_before_it_is_published()
    {
        var item = Processing();
        _redaction = _redaction with { SensitiveScore = 0.62, FramesClassified = 20 };

        await Processor(new SensitiveDetectionOptions { Threshold = 0.35 }).ProcessAsync(Job(item), isLastAttempt: false, Ct);

        await _media.Received(1).SetSensitiveAsync(
            item.Id, true, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), SensitiveMarkSource.Automatic, 0.62, Arg.Any<bool>());
        Assert.Equal(0.62, item.SensitiveScore);
        Assert.True(item.IsRestricted);
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
        Assert.DoesNotContain(_storage.Uploads, u => u.Bucket == MediaBucket.Public && u.Key.EndsWith("720p.mp4"));
    }

    [Fact]
    public async Task Media_scoring_under_the_threshold_is_published_and_its_score_kept()
    {
        var item = Processing();
        _redaction = _redaction with { SensitiveScore = 0.2, FramesClassified = 20 };

        await Processor(new SensitiveDetectionOptions { Threshold = 0.35 }).ProcessAsync(Job(item), isLastAttempt: false, Ct);

        await _media.DidNotReceiveWithAnyArgs().SetSensitiveAsync(default, default, default, Ct, default, default, default);
        Assert.Equal(0.2, item.SensitiveScore);
        Assert.False(item.IsRestricted);
    }

    [Fact]
    public async Task A_classifier_failure_sends_the_media_to_review_and_nothing_is_public()
    {
        var item = Processing();
        _redaction = _redaction with { ReviewReasons = ["The graphic content check failed."] };

        await Processor(new SensitiveDetectionOptions()).ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(MediaStatus.NeedsReview, item.Status);
        Assert.Empty(_storage.Keys(MediaBucket.Public));
    }

    // Tagging and untagging a post after publishing (visibility jobs)

    [Fact]
    public async Task Tagging_moves_the_files_to_private_storage_makes_a_preview_and_then_takes_the_public_copies_down()
    {
        var item = PublicReady();
        _sensitive = true;
        var savedWhileFilesWerePublic = false;
        _media.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            savedWhileFilesWerePublic = _storage.Has(MediaBucket.Public, item.PlaybackKey!);
            return Task.CompletedTask;
        });

        var outcome = await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.True(item.IsRestricted);
        Assert.True(savedWhileFilesWerePublic, "The state must be saved before the public copies are deleted.");
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
        Assert.Equal(new[] { $"{Folder(item)}/sensitive/720p.mp4", $"{Folder(item)}/sensitive/thumbnail.jpg" }, _storage.Keys(MediaBucket.Sensitive));
        await _purger.Received(1).PurgeAsync(
            item.Id,
            Arg.Is<IReadOnlyCollection<Uri>>(u => u.Count == 2 && u.Any(x => x.AbsolutePath.EndsWith("/720p.mp4"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Untagging_publishes_the_files_again_and_removes_the_preview()
    {
        var item = PublicReady();
        _sensitive = true;
        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);
        _sensitive = false;

        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.False(item.IsRestricted);
        Assert.Null(item.PreviewKey);
        Assert.Equal(new[] { $"{Folder(item)}/720p.mp4", $"{Folder(item)}/thumbnail.jpg" }, _storage.Keys(MediaBucket.Public));
        // Private copies are kept (so a racing tag job can never lose the files); they go with the media.
        Assert.Equal(2, _storage.Keys(MediaBucket.Sensitive).Count);
    }

    [Fact]
    public async Task A_job_that_stopped_after_saving_finishes_the_take_down_when_run_again()
    {
        var item = PublicReady();
        _sensitive = true;
        _storage.Put(MediaBucket.Sensitive, MediaKeys.Sensitive(item.PlaybackKey!));
        _storage.Put(MediaBucket.Sensitive, MediaKeys.Sensitive(item.ThumbnailKey!));
        _storage.Put(MediaBucket.Public, MediaKeys.Preview(UserId, item.Id));
        item.Restrict(MediaKeys.Preview(UserId, item.Id), Now);
        _media.ClearReceivedCalls();

        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
        Assert.Empty(_storage.Copies);
        await _transcoder.DidNotReceiveWithAnyArgs().CreateSensitivePreviewAsync(default!, default!, Ct);
        await _media.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Running_the_same_job_twice_changes_nothing_the_second_time()
    {
        var item = PublicReady();
        _sensitive = true;
        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);
        var copies = _storage.Copies.Count;
        var uploads = _storage.Uploads.Count;

        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(copies, _storage.Copies.Count);
        Assert.Equal(uploads, _storage.Uploads.Count);
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
    }

    [Fact]
    public async Task Untagged_while_the_job_ran_the_public_copies_stay()
    {
        var item = PublicReady();
        var reads = 0;
        // Sensitive when the job starts; untagged by the time it checks before deleting.
        _media.IsSensitiveAsync(item.Id, Arg.Any<CancellationToken>()).Returns(_ => ++reads == 1);

        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.False(item.IsRestricted);
        Assert.True(_storage.Has(MediaBucket.Public, item.PlaybackKey!));
        Assert.True(_storage.Has(MediaBucket.Public, item.ThumbnailKey!));
        Assert.False(_storage.Has(MediaBucket.Public, MediaKeys.Preview(UserId, item.Id)));
    }

    [Fact]
    public async Task A_clash_with_another_change_starts_the_job_again_from_the_saved_state()
    {
        var item = PublicReady();
        _sensitive = true;
        var saves = 0;
        _media.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            ++saves == 1 ? Task.FromException(new DbUpdateConcurrencyException("changed")) : Task.CompletedTask);

        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        await _media.Received(1).ReloadAsync(item, Arg.Any<CancellationToken>());
        Assert.True(item.IsRestricted);
        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
    }

    [Fact]
    public async Task A_visibility_job_does_nothing_to_media_that_is_not_ready()
    {
        var item = Processing();
        _sensitive = true;

        var outcome = await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Empty(_storage.Copies);
        Assert.Equal(MediaStatus.Processing, item.Status);
    }

    [Fact]
    public async Task A_failing_visibility_job_is_retried_and_never_fails_the_media()
    {
        var item = PublicReady();
        _sensitive = true;
        _storage.FailCopies = true;

        var outcome = await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: true, Ct);

        Assert.Equal(ProcessingOutcome.Retry, outcome);
        Assert.Equal(MediaStatus.Ready, item.Status);
        Assert.True(_storage.Has(MediaBucket.Public, item.PlaybackKey!));
    }

    // Originals aren't kept once a post is published: moderators' extra blurs work from the processed file

    [Fact]
    public async Task Adding_blur_regions_reprocesses_from_the_processed_file_when_the_original_is_gone()
    {
        var item = PublicReady();
        await _storage.DeleteOriginalAsync(item.OriginalKey, Ct);
        item.MarkOriginalDeleted(Now);
        var caller = Substitute.For<BJK.Modules.Shared.Security.ICurrentUser>();
        caller.IdentityId.Returns(Guid.NewGuid().ToString());
        caller.HasScope(MediaModule.ManageScope).Returns(true);
        caller.HasRole(BJK.Modules.Shared.Security.StaffRoles.Moderator).Returns(true);
        caller.UsedMfa.Returns(true);
        var handler = new BJK.Modules.Media.UseCases.Review.AddManualRedactionsCommandHandler(
            _media, _storage, _queue, Substitute.For<BJK.Modules.Shared.Moderation.IModerationAuditLog>(), caller, new FixedTimeProvider(Now));

        await handler.Handle(
            new BJK.Modules.Media.UseCases.Review.AddManualRedactionsCommand(item.Id, [new ManualRedaction(0.1, 0.1, 0.2, 0.2)]), Ct);

        var rework = $"{Folder(item)}/rework/r1.mp4";
        Assert.Equal(rework, item.SourceKey);
        Assert.Contains((MediaBucket.Public, $"{Folder(item)}/720p.mp4", MediaBucket.Originals, rework), _storage.Copies);
        Assert.Empty(_storage.Keys(MediaBucket.Public));

        var outcome = await Processor().ProcessAsync(Job(item), isLastAttempt: false, Ct);

        Assert.Equal(ProcessingOutcome.Completed, outcome);
        Assert.Equal(MediaStatus.NeedsReview, item.Status);
        Assert.Equal(1, item.Revision);
        await _redactor.Received(1).RedactVideoAsync(
            Arg.Is<string>(p => p.EndsWith("original.mp4")), Arg.Any<ProbeResult>(), Arg.Is<IReadOnlyList<ManualRedaction>>(r => r.Count == 1), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.True(_storage.Has(MediaBucket.Originals, $"{Folder(item)}/review/720p-r1.mp4"));
    }

    [Fact]
    public async Task A_photos_feed_copy_moves_with_its_other_files()
    {
        var item = MediaItem.Create(UserId, "parking.jpg", "image/jpeg", Now);
        item.MarkUploaded(10, Now, TimeSpan.FromDays(1));
        item.MarkReady(99, null, 2048, 1536, null, Now);
        _media.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        foreach (var key in new[] { item.PlaybackKey!, item.ThumbnailKey!, item.FeedKey! })
        {
            _storage.Put(MediaBucket.Public, key);
        }

        _sensitive = true;
        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(new[] { $"{Folder(item)}/preview.webp" }, _storage.Keys(MediaBucket.Public));
        Assert.Contains($"{Folder(item)}/sensitive/feed.webp", _storage.Keys(MediaBucket.Sensitive));

        _sensitive = false;
        await Processor().ProcessAsync(MediaProcessingJob.ForVisibility(item), isLastAttempt: false, Ct);

        Assert.Equal(new[] { $"{Folder(item)}/feed.webp", $"{Folder(item)}/image.webp", $"{Folder(item)}/thumbnail.webp" }, _storage.Keys(MediaBucket.Public));
    }

    [Fact]
    public void Visibility_jobs_have_their_own_message_ids()
    {
        var item = PublicReady();

        var first = MediaProcessingJob.ForVisibility(item);
        var second = MediaProcessingJob.ForVisibility(item);

        Assert.StartsWith($"{item.Id:D}/visibility/", first.MessageId);
        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    [Fact]
    public void Sensitive_keys_sit_in_a_sensitive_folder_beside_the_published_name()
    {
        Assert.Equal("u/m/sensitive/720p-r2.mp4", MediaKeys.Sensitive("u/m/720p-r2.mp4"));
        Assert.Equal($"{UserId:D}/{Guid.Empty:D}/preview-r1.webp", MediaKeys.Preview(UserId, Guid.Empty, 1));
    }
}

/// <summary>The three buckets in memory: which keys exist, plus every upload and copy made.</summary>
internal sealed class MemoryStorage : IMediaStorage
{
    private readonly Dictionary<MediaBucket, SortedSet<string>> _buckets = new()
    {
        [MediaBucket.Originals] = new(StringComparer.Ordinal),
        [MediaBucket.Public] = new(StringComparer.Ordinal),
        [MediaBucket.Sensitive] = new(StringComparer.Ordinal),
    };

    public List<(MediaBucket Bucket, string Key)> Uploads { get; } = [];

    public List<(MediaBucket From, string FromKey, MediaBucket To, string ToKey)> Copies { get; } = [];

    public bool FailCopies { get; set; }

    public void Put(MediaBucket bucket, string key) => _buckets[bucket].Add(key);

    public bool Has(MediaBucket bucket, string key) => _buckets[bucket].Contains(key);

    public List<string> Keys(MediaBucket bucket) => [.. _buckets[bucket]];

    public Task<SignedUpload> CreateOriginalUploadAsync(string key, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredFile?> FindOriginalAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<StoredFile?>(Has(MediaBucket.Originals, key) ? new StoredFile(10, null) : null);

    public Task DeleteOriginalAsync(string key, CancellationToken cancellationToken) => Remove(MediaBucket.Originals, [key]);

    public Task DownloadOriginalAsync(string key, Stream destination, CancellationToken cancellationToken) =>
        DownloadAsync(MediaBucket.Originals, key, destination, cancellationToken);

    public Task UploadPublicAsync(string key, string filePath, string contentType, CancellationToken cancellationToken) =>
        UploadAsync(MediaBucket.Public, key, filePath, contentType, cancellationToken);

    public Task DeletePublicAsync(IEnumerable<string> keys, CancellationToken cancellationToken) => Remove(MediaBucket.Public, keys);

    public Task UploadPrivateAsync(string key, string filePath, string contentType, CancellationToken cancellationToken) =>
        UploadAsync(MediaBucket.Originals, key, filePath, contentType, cancellationToken);

    public Task PublishPrivateAsync(string privateKey, string publicKey, string contentType, CancellationToken cancellationToken) =>
        CopyAsync(MediaBucket.Originals, privateKey, MediaBucket.Public, publicKey, contentType, cancellationToken);

    public Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var bucket in _buckets.Values)
        {
            bucket.RemoveWhere(k => k.StartsWith(MediaKeys.UserFolder(userId), StringComparison.Ordinal));
        }

        return Task.CompletedTask;
    }

    public Task<Uri> CreateOriginalDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        CreateDownloadUrlAsync(MediaBucket.Originals, key, lifetime, cancellationToken);

    public Uri PublicUrl(string key) => new("https://media.example.com/" + key);

    public Task UploadAsync(MediaBucket bucket, string key, string filePath, string contentType, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(filePath);
        }

        Uploads.Add((bucket, key));
        Put(bucket, key);
        return Task.CompletedTask;
    }

    public async Task DownloadAsync(MediaBucket bucket, string key, Stream destination, CancellationToken cancellationToken)
    {
        if (!Has(bucket, key))
        {
            throw new FileNotFoundException($"{bucket}/{key}");
        }

        await destination.WriteAsync("file"u8.ToArray(), cancellationToken);
    }

    public Task CopyAsync(MediaBucket from, string fromKey, MediaBucket to, string toKey, string contentType, CancellationToken cancellationToken)
    {
        if (FailCopies)
        {
            throw new IOException("Storage is down.");
        }

        if (!Has(from, fromKey))
        {
            throw new FileNotFoundException($"{from}/{fromKey}");
        }

        Copies.Add((from, fromKey, to, toKey));
        Put(to, toKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(MediaBucket bucket, string key, CancellationToken cancellationToken) => Task.FromResult(Has(bucket, key));

    public Task DeleteAsync(MediaBucket bucket, IEnumerable<string> keys, CancellationToken cancellationToken) => Remove(bucket, keys);

    public Task<Uri> CreateDownloadUrlAsync(MediaBucket bucket, string key, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://signed.example.com/{bucket}/{key}?X-Amz-Expires={(int)lifetime.TotalSeconds}"));

    private Task Remove(MediaBucket bucket, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            _buckets[bucket].Remove(key);
        }

        return Task.CompletedTask;
    }
}
