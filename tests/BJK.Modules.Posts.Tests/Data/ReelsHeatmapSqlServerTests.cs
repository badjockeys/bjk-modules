using System.Globalization;
using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Heatmap;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Data;

/// <summary>
/// Reels and the heat map against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to run locally): the
/// ranking refresh, cursor paging, sensitive and blocked filtering, idempotent plays with daily counts, the retention
/// sweep, and heat map cells, suppression, filters (UK time) and area figures. Uses a new database, dropped afterwards.
/// </summary>
public sealed class ReelsHeatmapSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_reels_test_" + Guid.NewGuid().ToString("N")[..12];
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

    private PostsDbContext Context()
    {
        if (_connectionString is null)
        {
            Assert.Skip("BJK_TEST_SQLSERVER isn't set.");
        }

        return new PostsDbContext(new DbContextOptionsBuilder<PostsDbContext>()
            .UseSqlServer(_connectionString, sql =>
            {
                // As the module configures it: a deadlock between simultaneous plays is retried.
                sql.MigrationsHistoryTable(PostsModule.MigrationsHistoryTable, PostsModule.Schema);
                sql.EnableRetryOnFailure();
            })
            .Options);
    }

    private static PostDetails At(decimal lat, decimal lng, DateTimeOffset incidentAt, IncidentCategory category = IncidentCategory.Tailgating, string? place = "A57 Warrington Road, Prescot") =>
        new("Incident", null, category, incidentAt, lat, lng, place);

    private async Task SaveAsync(params object[] entities)
    {
        await using var db = Context();
        db.AddRange(entities);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Reels_are_public_videos_ranked_recent_and_popular_with_cursor_paging()
    {
        var old = Post.Create(OtherUserId, Details("Old"), [Guid.NewGuid()], Now.AddHours(-30), hasVideo: true);
        var popular = Post.Create(OtherUserId, Details("Popular"), [Guid.NewGuid()], Now.AddHours(-20), hasVideo: true);
        var fresh = Post.Create(OtherUserId, Details("Fresh"), [Guid.NewGuid()], Now.AddHours(-1), hasVideo: true);
        var sensitive = Post.Create(OtherUserId, Details("Sensitive"), [Guid.NewGuid()], Now.AddMinutes(-30), isSensitive: true, hasVideo: true);
        var mineSensitive = Post.Create(UserId, Details("My sensitive"), [Guid.NewGuid()], Now.AddMinutes(-40), isSensitive: true, hasVideo: true);
        var photos = Post.Create(OtherUserId, Details("Photos"), [Guid.NewGuid()], Now.AddMinutes(-10));
        var ancient = Post.Create(OtherUserId, Details("Ancient"), [Guid.NewGuid()], Now.AddDays(-60), hasVideo: true);
        var reactions = Enumerable.Range(0, 40).Select(_ => (object)Reaction.Create(popular.Id, Guid.NewGuid(), ReactionType.Like, Now)).ToArray();
        await SaveAsync(old, popular, fresh, sensitive, mineSensitive, photos, ancient);
        await SaveAsync(reactions);

        await using (var db = Context())
        {
            var scored = await new ReelRepository(db).RefreshScoresAsync(Now, Now.AddDays(-30), Ct);
            Assert.Equal(5, scored);
        }

        await using (var db = Context())
        {
            var repository = new ReelRepository(db);
            var everyone = await repository.ListAsync(new ReelSearch(null, false, null, null, null), 10, Ct);
            Assert.Equal(new[] { "Popular", "Fresh", "Old", "Ancient" }, everyone.Select(p => p.Title));
            Assert.Equal(ReelRanking.Score(40, 0, TimeSpan.FromHours(20)), everyone[0].ReelScore, 6);
            Assert.Equal(0, everyone[3].ReelScore);

            // The author sees their own sensitive reel; age-verified viewers see all of them.
            var author = await repository.ListAsync(new ReelSearch(null, false, UserId, null, null), 10, Ct);
            Assert.Contains(author, p => p.Title == "My sensitive");
            Assert.DoesNotContain(author, p => p.Title == "Sensitive");
            var verified = await repository.ListAsync(new ReelSearch(null, true, UserId, null, null), 10, Ct);
            Assert.Equal(6, verified.Count);

            // Blocked authors are left out; a cursor continues after the last reel.
            Assert.Empty(await repository.ListAsync(new ReelSearch(null, true, UserId, [OtherUserId, UserId], null), 10, Ct));
            var first = await repository.ListAsync(new ReelSearch(null, false, null, null, null), 2, Ct);
            var next = await repository.ListAsync(
                new ReelSearch(null, false, null, null, new ReelCursor(first[^1].ReelScore, first[^1].CreatedAt)),
                10,
                Ct);
            Assert.Equal(new[] { "Old", "Ancient" }, next.Select(p => p.Title));
            Assert.Empty(await repository.ListAsync(new ReelSearch(IncidentCategory.BadParking, true, null, null, null), 10, Ct));
        }
    }

    [Fact]
    public async Task Plays_are_counted_once_per_viewer_post_and_day_and_old_ones_are_swept()
    {
        var reel = Post.Create(OtherUserId, Details(), [Guid.NewGuid()], Now, hasVideo: true);
        await SaveAsync(reel);
        var today = UkCalendar.Today(Now);
        var user = ReelViewer.ForUser(UserId);
        var anonymous = ReelViewer.ForAnonymous("web-viewer-0123456789", null, null)!;

        await using (var db = Context())
        {
            var repository = new ReelRepository(db);
            Assert.True(await repository.RecordPlayAsync(new ReelPlay(user, today, reel.Id, Now), Ct));
            Assert.False(await repository.RecordPlayAsync(new ReelPlay(user, today, reel.Id, Now.AddMinutes(1)), Ct));
            Assert.True(await repository.RecordPlayAsync(new ReelPlay(anonymous, today, reel.Id, Now), Ct));
            Assert.True(await repository.RecordPlayAsync(new ReelPlay(user, today.AddDays(-40), reel.Id, Now.AddDays(-40)), Ct));
            Assert.True(await repository.RecordPlayAsync(new ReelPlay(anonymous, today.AddDays(-3), reel.Id, Now.AddDays(-3)), Ct));

            // Several reports of one play at the same moment still count once.
            var other = ReelViewer.ForAnonymous("another-viewer-0001", null, null)!;
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            {
                await using var parallel = Context();
                return await new ReelRepository(parallel).RecordPlayAsync(new ReelPlay(other, today, reel.Id, Now), Ct);
            }));
            Assert.Equal(1, results.Count(r => r));

            Assert.Equal(1, await repository.CountPlaysAsync(UserId, today, Ct));
            Assert.True(await repository.HasPlayedAsync(UserId, today, reel.Id, Ct));
        }

        await using (var db = Context())
        {
            var counts = await db.ReelPlayDailyCounts.Where(c => c.PostId == reel.Id).ToDictionaryAsync(c => c.Day, c => c.Plays, Ct);
            Assert.Equal(3, counts[today]);
            Assert.Equal(1, counts[today.AddDays(-40)]);

            var deleted = await new ReelRepository(db).DeletePlaysBeforeAsync(today.AddDays(-34), today.AddDays(-1), Ct);
            Assert.Equal(2, deleted);
            Assert.Equal(3, await db.ReelPlays.CountAsync(Ct));
            Assert.Equal(3, await db.ReelPlayDailyCounts.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task The_heat_map_counts_public_pins_by_cell_and_hides_small_cells()
    {
        // Prescot: 4 tailgating incidents at one rounded pin on weekday mornings, 2 at another (hidden), 3 bad parking
        // on a Saturday evening, plus posts that must never count: hidden, still processing, outside the period.
        var monday = new DateTimeOffset(2026, 10, 12, 7, 30, 0, TimeSpan.Zero); // 8:30 UK time (BST)
        var saturday = new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero); // 19:00 UK time
        var posts = new List<Post>();
        for (var i = 0; i < 4; i++)
        {
            posts.Add(Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday.AddDays(i)), [Guid.NewGuid()], Now));
        }

        for (var i = 0; i < 2; i++)
        {
            posts.Add(Post.Create(Guid.NewGuid(), At(53.4471m, -2.7700m, monday), [Guid.NewGuid()], Now));
        }

        for (var i = 0; i < 3; i++)
        {
            posts.Add(Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, saturday, IncidentCategory.BadParking, "Market Place, Prescot"), [Guid.NewGuid()], Now, isSensitive: true));
        }

        var hidden = Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday), [Guid.NewGuid()], Now);
        hidden.Hide("Reported", Now);
        posts.Add(hidden);
        posts.Add(Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday), [Guid.NewGuid()], Now, mediaPending: true));
        posts.Add(Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday.AddDays(-30)), [Guid.NewGuid()], Now));
        await SaveAsync(posts.ToArray());

        var box = new GeoBox(53.40m, 53.46m, -2.85m, -2.75m);
        var (from, to) = (UkCalendar.StartOf(new DateOnly(2026, 10, 12)), UkCalendar.StartOf(new DateOnly(2026, 10, 19)));
        HeatmapFilter Filter(IncidentCategory[]? categories = null, HourRange? hours = null, HeatmapDays days = HeatmapDays.All) =>
            new(box, from, to, categories ?? Array.Empty<IncidentCategory>(), hours, days);

        await using var db = Context();
        var heatmap = new HeatmapRepository(db);

        var cells = await heatmap.CountCellsAsync(Filter(), 0.0045m, 0.0072m, 3, 100, Ct);
        var cell = Assert.Single(cells);
        Assert.Equal(7, cell.Count);
        Assert.Equal(HeatmapGrid.CellIndex(53.4291m, 0.0045m), cell.CellY);

        // At a lower zoom both pins share a 10 km cell.
        Assert.Equal(9, Assert.Single(await heatmap.CountCellsAsync(Filter(), 0.09m, 0.144m, 3, 100, Ct)).Count);

        Assert.Equal(4, Assert.Single(await heatmap.CountCellsAsync(Filter([IncidentCategory.Tailgating]), 0.0045m, 0.0072m, 3, 100, Ct)).Count);
        Assert.Equal(4, Assert.Single(await heatmap.CountCellsAsync(Filter(hours: new HourRange(7, 10)), 0.0045m, 0.0072m, 3, 100, Ct)).Count);
        Assert.Equal(3, Assert.Single(await heatmap.CountCellsAsync(Filter(days: HeatmapDays.Weekends), 0.0045m, 0.0072m, 3, 100, Ct)).Count);
        Assert.Empty(await heatmap.CountCellsAsync(Filter(hours: new HourRange(22, 6)), 0.0045m, 0.0072m, 3, 100, Ct));

        Assert.Equal(9, await heatmap.CountAsync(Filter(), Ct));
        var hours = await heatmap.CountByHourAsync(Filter(), Ct);
        Assert.Equal(6, hours.Single(h => h.Key == "8").Count);
        Assert.Equal(3, hours.Single(h => h.Key == "19").Count);
        var categories = await heatmap.CountByCategoryAsync(Filter(), Ct);
        Assert.Equal("Tailgating", categories[0].Key);
        Assert.Equal(6, categories[0].Count);
        var places = await heatmap.CountByPlaceAsync(Filter(), 3, Ct);
        Assert.Equal(new[] { ("A57 Warrington Road, Prescot", 6), ("Market Place, Prescot", 3) }, places.Select(p => (p.Key, p.Count)));
    }

    [Fact]
    public async Task The_heat_map_counts_a_post_under_each_of_its_types()
    {
        var monday = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.FromHours(1));
        var posts = Enumerable.Range(0, 3)
            .Select(i => Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday.AddHours(i)) with { Types = [IncidentCategory.Speeding, IncidentCategory.MobilePhoneUse] }, [Guid.NewGuid()], Now))
            .Append(Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, monday, IncidentCategory.Speeding), [Guid.NewGuid()], Now))
            .ToArray();
        await SaveAsync(posts);

        var box = new GeoBox(53.40m, 53.46m, -2.85m, -2.75m);
        var (from, to) = (UkCalendar.StartOf(new DateOnly(2026, 10, 12)), UkCalendar.StartOf(new DateOnly(2026, 10, 19)));
        HeatmapFilter Filter(params IncidentCategory[] types) => new(box, from, to, types, null, HeatmapDays.All);

        await using var db = Context();
        var heatmap = new HeatmapRepository(db);

        Assert.Equal(4, await heatmap.CountAsync(Filter(), Ct));
        Assert.Equal(3, await heatmap.CountAsync(Filter(IncidentCategory.MobilePhoneUse), Ct));
        Assert.Equal(4, await heatmap.CountAsync(Filter(IncidentCategory.MobilePhoneUse, IncidentCategory.Speeding), Ct));
        Assert.Equal(0, await heatmap.CountAsync(Filter(IncidentCategory.BadParking), Ct));

        var types = await heatmap.CountByCategoryAsync(Filter(), Ct);
        Assert.Equal(new[] { ("Speeding", 4), ("MobilePhoneUse", 3) }, types.Select(t => (t.Key, t.Count)));
    }

    [Fact]
    public async Task Category_trends_count_each_type_by_UK_day_week_and_month()
    {
        // 23:30 UTC on Sunday 11 October is 00:30 on Monday 12 October in the UK (British Summer Time).
        var lateSunday = new DateTimeOffset(2026, 10, 11, 23, 30, 0, TimeSpan.Zero);
        var tuesday = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.FromHours(1));
        var september = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.FromHours(1));
        await SaveAsync(
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, lateSunday) with { Types = [IncidentCategory.Speeding, IncidentCategory.Tailgating] }, [Guid.NewGuid()], Now),
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, tuesday, IncidentCategory.Speeding), [Guid.NewGuid()], Now),
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, september, IncidentCategory.Speeding), [Guid.NewGuid()], Now));

        var everywhere = new GeoBox(-90m, 90m, -180m, 180m);
        var filter = new HeatmapFilter(everywhere, UkCalendar.StartOf(new DateOnly(2026, 9, 1)), UkCalendar.StartOf(new DateOnly(2026, 10, 19)), [], null, HeatmapDays.All);

        await using var db = Context();
        var heatmap = new HeatmapRepository(db);

        var days = await heatmap.CountByCategoryAndPeriodAsync(filter, TrendPeriod.Day, Ct);
        Assert.Equal(
            new[] { ("Speeding", "2026-09-30", 1), ("Speeding", "2026-10-12", 1), ("Tailgating", "2026-10-12", 1), ("Speeding", "2026-10-13", 1) },
            days.Select(r => (r.Key, r.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Count)));

        var weeks = await heatmap.CountByCategoryAndPeriodAsync(filter, TrendPeriod.Week, Ct);
        Assert.Equal(
            new[] { ("Speeding", "2026-09-28", 1), ("Speeding", "2026-10-12", 2), ("Tailgating", "2026-10-12", 1) },
            weeks.Select(r => (r.Key, r.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Count)));

        var months = await heatmap.CountByCategoryAndPeriodAsync(filter, TrendPeriod.Month, Ct);
        Assert.Equal(
            new[] { ("Speeding", "2026-09-01", 1), ("Speeding", "2026-10-01", 2), ("Tailgating", "2026-10-01", 1) },
            months.Select(r => (r.Key, r.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Count)));
    }

    [Fact]
    public async Task The_heat_map_filters_by_vehicle_light_school_run_reported_and_video()
    {
        // Monday 12 Oct 2026: 08:30 BST (school run, daylight) and 19:30 BST (dark).
        var morning = new DateTimeOffset(2026, 10, 12, 7, 30, 0, TimeSpan.Zero);
        var evening = new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.Zero);
        var van = new VehicleDetails(VehicleType.Van, "Ford", "Transit", VehicleColour.White);
        var car = new VehicleDetails(VehicleType.Car, "BMW", "3 Series", VehicleColour.Black);
        var posts = new List<Post>
        {
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, morning) with { Vehicle = van, ReportedToPolice = true }, [Guid.NewGuid()], Now),
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, morning.AddMinutes(5)) with { Vehicle = van }, [Guid.NewGuid()], Now, hasVideo: true),
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, evening) with { Vehicle = car }, [Guid.NewGuid()], Now),
            Post.Create(Guid.NewGuid(), At(53.4291m, -2.8015m, evening.AddMinutes(5)), [Guid.NewGuid()], Now),
        };
        await SaveAsync(posts.ToArray());

        var box = new GeoBox(53.40m, 53.46m, -2.85m, -2.75m);
        var (from, to) = (UkCalendar.StartOf(new DateOnly(2026, 10, 12)), UkCalendar.StartOf(new DateOnly(2026, 10, 13)));
        HeatmapFilter Filter() => new(box, from, to, [], null, HeatmapDays.All);

        await using var db = Context();
        var heatmap = new HeatmapRepository(db);

        Assert.Equal(4, await heatmap.CountAsync(Filter(), Ct));
        Assert.Equal(2, await heatmap.CountAsync(Filter() with { VehicleTypes = [VehicleType.Van] }, Ct));
        Assert.Equal(2, await heatmap.CountAsync(Filter() with { Make = "Ford", Model = "Transit" }, Ct));
        Assert.Equal(1, await heatmap.CountAsync(Filter() with { Colours = [VehicleColour.Black, VehicleColour.Red] }, Ct));
        Assert.Equal(2, await heatmap.CountAsync(Filter() with { Dark = true }, Ct));
        Assert.Equal(2, await heatmap.CountAsync(Filter() with { Dark = false }, Ct));
        Assert.Equal(2, await heatmap.CountAsync(Filter() with { SchoolRun = true }, Ct));
        Assert.Equal(1, await heatmap.CountAsync(Filter() with { ReportedOnly = true }, Ct));
        Assert.Equal(1, await heatmap.CountAsync(Filter() with { VideoOnly = true }, Ct));
    }
}
