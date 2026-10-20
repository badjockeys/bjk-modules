using BJK.Modules.Media.Contracts;
using BJK.Modules.Media.Domain;
using BJK.Modules.Media.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BJK.Modules.Media.Tests.TestData;

namespace BJK.Modules.Media.Tests.Data;

/// <summary>
/// Against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to a connection string without a database
/// name to run locally): the migrations apply, status changes land in the outbox in the same save, and the segment
/// progress updates are safe when several workers race. Each test uses a new database, dropped afterwards.
/// </summary>
public sealed class MediaSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_media_test_" + Guid.NewGuid().ToString("N")[..12];
    private string? _connectionString;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("BJK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        _connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = _database }.ConnectionString;
        await using var db = Context();
        await db.Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is not null)
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }

    private MediaDbContext Context()
    {
        if (_connectionString is null)
        {
            Assert.Skip("BJK_TEST_SQLSERVER isn't set.");
        }

        return new MediaDbContext(new DbContextOptionsBuilder<MediaDbContext>()
            .UseSqlServer(_connectionString, sql => sql.MigrationsHistoryTable(MediaModule.MigrationsHistoryTable, MediaModule.Schema))
            .Options);
    }

    private async Task<MediaItem> SavedProcessingVideo()
    {
        await using var db = Context();
        var item = MediaItem.Create(UserId, "clip.mp4", "video/mp4", Now);
        item.MarkUploaded(100, Now, TimeSpan.FromDays(90));
        db.Media.Add(item);
        await db.SaveChangesAsync(Ct);
        return item;
    }

    [Fact]
    public async Task A_status_change_is_saved_to_the_outbox_in_the_same_save()
    {
        var saved = await SavedProcessingVideo();

        await using (var db = Context())
        {
            var item = await db.Media.SingleAsync(m => m.Id == saved.Id, Ct);
            item.MarkFailed("Videos can be up to 30 seconds long.", Now);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var message = await db.Outbox.SingleAsync(Ct);
            Assert.Equal(MediaEvents.FailedType, message.Type);
            Assert.Null(message.PublishedAt);
            var mediaEvent = System.Text.Json.JsonSerializer.Deserialize<MediaEvent>(message.Payload, BJK.Modules.Shared.Messaging.OutboxMessage.JsonOptions)!;
            Assert.Equal((saved.Id, MediaEventType.Failed, "Videos can be up to 30 seconds long."), (mediaEvent.MediaId, mediaEvent.Type, mediaEvent.Reason));
            Assert.Equal(message.Id, mediaEvent.EventId);
        }
    }

    [Fact]
    public async Task Sensitive_marks_are_made_once_kept_when_automatic_and_cleared_only_as_allowed()
    {
        var item = await SavedProcessingVideo();

        await using (var db = Context())
        {
            var media = new MediaRepository(db);
            Assert.True(await media.SetSensitiveAsync(item.Id, true, Now, Ct));
            Assert.False(await media.SetSensitiveAsync(item.Id, true, Now, Ct));
            Assert.True(await media.IsSensitiveAsync(item.Id, Ct));

            // Automatic detection upgrades a post mark, so the author can no longer clear it.
            Assert.False(await media.SetSensitiveAsync(item.Id, true, Now, Ct, SensitiveMarkSource.Automatic, 0.7));
            var mark = (await media.GetSensitiveMarksAsync([item.Id], Ct))[item.Id];
            Assert.Equal((SensitiveMarkSource.Automatic, 0.7), (mark.Source, mark.Score));

            Assert.False(await media.SetSensitiveAsync(item.Id, false, Now, Ct));
            Assert.True(await media.IsSensitiveAsync(item.Id, Ct));
            Assert.True(await media.SetSensitiveAsync(item.Id, false, Now, Ct, clearAutomatic: true));
            Assert.False(await media.IsSensitiveAsync(item.Id, Ct));
        }

        // Two marks at once: one inserts, the other does nothing.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = Context();
            return await new MediaRepository(db).SetSensitiveAsync(item.Id, true, Now, Ct);
        }));
        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Originals_are_swept_only_when_finished_and_past_their_deadline()
    {
        await using (var db = Context())
        {
            MediaItem Uploaded(string name, DateTimeOffset at)
            {
                var m = MediaItem.Create(UserId, name, "video/mp4", at);
                m.MarkUploaded(10, at, TimeSpan.FromHours(24));
                db.Media.Add(m);
                return m;
            }

            var failedOld = Uploaded("failed-old.mp4", Now.AddHours(-25));
            failedOld.MarkFailed("Processing failed.", Now.AddHours(-25));
            var failedNew = Uploaded("failed-new.mp4", Now.AddHours(-2));
            failedNew.MarkFailed("Processing failed.", Now.AddHours(-2));
            var readyOld = Uploaded("ready-old.mp4", Now.AddHours(-30));
            readyOld.MarkReady(10, 10m, 1280, 720, null, Now.AddHours(-30));
            var released = Uploaded("released.mp4", Now.AddHours(-30));
            released.MarkReady(10, 10m, 1280, 720, null, Now.AddHours(-30));
            released.MarkOriginalDeleted(Now.AddHours(-29));
            Uploaded("stuck-processing.mp4", Now.AddDays(-3));
            var abandoned = MediaItem.Create(UserId, "abandoned.mp4", "video/mp4", Now.AddHours(-26));
            db.Media.Add(abandoned);
            db.Media.Add(MediaItem.Create(UserId, "uploading.mp4", "video/mp4", Now.AddHours(-1)));
            await db.SaveChangesAsync(Ct);

            var due = await new MediaRepository(db).ListOriginalsToDeleteAsync(Now, Now.AddHours(-24), 50, Ct);

            Assert.Equal(
                new[] { readyOld.Id, abandoned.Id, failedOld.Id }.Order(),
                due.Select(m => m.Id).Order());
        }
    }

    [Fact]
    public async Task Unused_upload_candidates_are_old_finished_or_abandoned_and_not_yet_found_in_use()
    {
        await using (var db = Context())
        {
            MediaItem Uploaded(string name, DateTimeOffset at)
            {
                var m = MediaItem.Create(UserId, name, "video/mp4", at);
                m.MarkUploaded(10, at, TimeSpan.FromHours(24));
                db.Media.Add(m);
                return m;
            }

            var readyOld = Uploaded("ready-old.mp4", Now.AddHours(-30));
            readyOld.MarkReady(10, 10m, 1280, 720, null, Now.AddHours(-30));
            var failedOld = Uploaded("failed-old.mp4", Now.AddHours(-25));
            failedOld.MarkFailed("Processing failed.", Now.AddHours(-25));
            var abandoned = MediaItem.Create(UserId, "abandoned.mp4", "video/mp4", Now.AddHours(-26));
            db.Media.Add(abandoned);
            var checkedOld = Uploaded("in-a-post.mp4", Now.AddHours(-40));
            checkedOld.MarkReady(10, 10m, 1280, 720, null, Now.AddHours(-40));
            checkedOld.MarkInUse(Now.AddHours(-10));
            var deletedOld = Uploaded("deleted.mp4", Now.AddHours(-50));
            deletedOld.Delete(Now.AddHours(-49));
            Uploaded("stuck-processing.mp4", Now.AddDays(-3));
            var readyNew = Uploaded("ready-new.mp4", Now.AddHours(-2));
            readyNew.MarkReady(10, 10m, 1280, 720, null, Now.AddHours(-2));
            await db.SaveChangesAsync(Ct);

            var candidates = await new MediaRepository(db).ListUnusedCandidatesAsync(Now.AddHours(-24), 50, Ct);

            Assert.Equal(new[] { readyOld.Id, abandoned.Id, failedOld.Id }, candidates.Select(m => m.Id));
        }
    }

    [Fact]
    public async Task Restricted_state_and_scores_are_saved()
    {
        var saved = await SavedProcessingVideo();

        await using (var db = Context())
        {
            var item = await db.Media.SingleAsync(m => m.Id == saved.Id, Ct);
            item.RecordSensitiveScore(0.41234);
            item.MarkReady(10, 10m, 1280, 720, null, Now);
            item.Restrict(MediaKeys.Preview(UserId, item.Id), Now);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var item = (await new MediaRepository(db).GetUntrackedAsync([saved.Id], Ct)).Single();
            Assert.True(item.IsRestricted);
            Assert.EndsWith("/preview.webp", item.PreviewKey);
            Assert.Equal(0.4123, item.SensitiveScore);
        }
    }

    [Fact]
    public async Task Segment_progress_is_created_once_completed_once_and_assembly_claimed_once()
    {
        var item = await SavedProcessingVideo();
        var plan = VideoSegmentPlan.For(12, 30, 0, 5, 1.0);

        await using (var db = Context())
        {
            Assert.True(await new VideoJobStore(db).TryCreateAsync(VideoJob.Create(item.Id, 0, plan, Now), Ct));
        }

        await using (var db = Context())
        {
            // A repeat delivery of the planning job.
            Assert.False(await new VideoJobStore(db).TryCreateAsync(VideoJob.Create(item.Id, 0, plan, Now), Ct));
        }

        var result = new VideoSegmentResult(150, 1, 0, ["1 detection(s) had low confidence."], TimeSpan.FromSeconds(3));
        foreach (var index in new[] { 0, 1 })
        {
            await using var db = Context();
            var store = new VideoJobStore(db);
            await store.RecordAttemptAsync(item.Id, 0, index, Ct);
            Assert.True(await store.TryCompleteAsync(item.Id, 0, index, result, Now, Ct));
            Assert.False(await store.TryCompleteAsync(item.Id, 0, index, result, Now, Ct));
            Assert.False(await store.TryClaimAssemblyAsync(item.Id, 0, Now, Ct));
            Assert.False(await store.AllDoneAsync(item.Id, 0, Ct));
        }

        await using (var db = Context())
        {
            Assert.True(await new VideoJobStore(db).TryCompleteAsync(item.Id, 0, 2, result with { Frames = 60 }, Now, Ct));
        }

        // Several workers see the last segment finish at once: exactly one wins the claim.
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = Context();
            return await new VideoJobStore(db).TryClaimAssemblyAsync(item.Id, 0, Now, Ct);
        }));
        Assert.Equal(1, claims.Count(c => c));

        await using (var db = Context())
        {
            var store = new VideoJobStore(db);
            Assert.True(await store.AllDoneAsync(item.Id, 0, Ct));
            var job = (await store.GetAsync(item.Id, 0, Ct))!;
            Assert.Equal(3, job.SegmentCount);
            Assert.All(job.Segments, s => Assert.True(s.IsDone));
            Assert.Equal(new[] { 150, 150, 60 }, job.Segments.OrderBy(s => s.Index).Select(s => s.Frames!.Value));
            Assert.Equal(1, job.Segments.Single(s => s.Index == 0).Attempts);
            Assert.Equal(new[] { "1 detection(s) had low confidence." }, job.Segments.Single(s => s.Index == 0).ReviewReasonList);
            Assert.NotNull(job.AssembleQueuedAt);
        }
    }

    [Fact]
    public async Task Racing_completions_of_one_segment_count_once()
    {
        var item = await SavedProcessingVideo();
        await using (var db = Context())
        {
            await new VideoJobStore(db).TryCreateAsync(VideoJob.Create(item.Id, 0, VideoSegmentPlan.For(12, 30, 0, 5, 1.0), Now), Ct);
        }

        var result = new VideoSegmentResult(150, 0, 0, [], TimeSpan.FromSeconds(1));
        var wins = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = Context();
            return await new VideoJobStore(db).TryCompleteAsync(item.Id, 0, 1, result, Now, Ct);
        }));

        Assert.Equal(1, wins.Count(w => w));
    }

    [Fact]
    public async Task Removing_the_media_row_removes_its_segment_progress()
    {
        var item = await SavedProcessingVideo();
        await using (var db = Context())
        {
            await new VideoJobStore(db).TryCreateAsync(VideoJob.Create(item.Id, 0, VideoSegmentPlan.For(12, 30, 0, 5, 1.0), Now), Ct);
        }

        await using (var db = Context())
        {
            await new MediaRepository(db).DeleteAllForUserAsync(UserId, Ct);
            Assert.False(await db.VideoJobs.AnyAsync(Ct));
            Assert.False(await db.VideoSegments.AnyAsync(Ct));
        }
    }
}
