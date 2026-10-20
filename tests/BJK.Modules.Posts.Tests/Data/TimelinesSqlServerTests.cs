using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Data;

/// <summary>
/// The home timelines against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to run locally): For you
/// ranks recent posts by score, boosts the home area and continues with older posts; Following and custom timelines
/// filter by authors, types and area. Uses a new database, dropped afterwards.
/// </summary>
public sealed class TimelinesSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_timelines_test_" + Guid.NewGuid().ToString("N")[..12];
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
                sql.MigrationsHistoryTable(PostsModule.MigrationsHistoryTable, PostsModule.Schema);
                sql.EnableRetryOnFailure();
            })
            .Options);
    }

    private static Post At(Guid author, string title, decimal lat, decimal lng, DateTimeOffset createdAt, IncidentCategory type = IncidentCategory.Speeding) =>
        Post.Create(author, Details(title) with { Category = type, Latitude = lat, Longitude = lng, Types = [type] }, [Guid.NewGuid()], createdAt);

    [Fact]
    public async Task For_you_ranks_recent_posts_boosts_the_home_area_then_continues_with_older_posts()
    {
        var (leeds, prescot) = ((53.80m, -1.55m), (53.43m, -2.80m));
        var popularFar = At(Guid.NewGuid(), "Popular in Leeds", leeds.Item1, leeds.Item2, Now.AddHours(-3));
        var quietNear = At(Guid.NewGuid(), "Quiet in Prescot", prescot.Item1, prescot.Item2, Now.AddHours(-3));
        var old = At(Guid.NewGuid(), "Old one", leeds.Item1, leeds.Item2, Now.AddDays(-60));
        var older = At(Guid.NewGuid(), "Older one", leeds.Item1, leeds.Item2, Now.AddDays(-90));
        await using (var db = Context())
        {
            db.Posts.AddRange(popularFar, quietNear, old, older);
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [posts].[Posts] SET [ReelScore] = 2.0 WHERE [Id] = {popularFar.Id}", Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [posts].[Posts] SET [ReelScore] = 1.0 WHERE [Id] = {quietNear.Id}", Ct);
        }

        await using var context = Context();
        var posts = new PostRepository(context);
        var since = Now.AddDays(-30);

        var anywhere = await posts.ForYouAsync(new ForYouSearch(since, null, null), 1, 3, Ct);
        Assert.Equal(4, anywhere.TotalCount);
        Assert.Equal(new[] { popularFar.Id, quietNear.Id, old.Id }, anywhere.Items.Select(p => p.Id));

        var home = new ForYouHome(GeoBox.Around(prescot.Item1, prescot.Item2, 5), GeoBox.Around(prescot.Item1, prescot.Item2, 20));
        var nearMe = await posts.ForYouAsync(new ForYouSearch(since, home, null), 1, 2, Ct);
        Assert.Equal(new[] { quietNear.Id, popularFar.Id }, nearMe.Items.Select(p => p.Id));

        // A page that straddles ranked and older posts, and blocked authors left out.
        var second = await posts.ForYouAsync(new ForYouSearch(since, home, [older.AuthorId]), 2, 2, Ct);
        Assert.Equal(3, second.TotalCount);
        Assert.Equal(new[] { old.Id }, second.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task Following_and_custom_timelines_filter_by_authors_types_and_area()
    {
        var friend = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var parkingInLeeds = At(friend, "Parking in Leeds", 53.80m, -1.55m, Now.AddHours(-1), IncidentCategory.BadParking);
        var speedingInLeeds = At(stranger, "Speeding in Leeds", 53.80m, -1.55m, Now.AddHours(-2));
        var parkingInYork = At(stranger, "Parking in York", 53.96m, -1.08m, Now.AddHours(-3), IncidentCategory.BadParking);
        await using (var db = Context())
        {
            db.Posts.AddRange(parkingInLeeds, speedingInLeeds, parkingInYork);
            await db.SaveChangesAsync(Ct);
        }

        await using var context = Context();
        var posts = new PostRepository(context);

        var following = await posts.SearchAsync(new PostSearch(null, null, null, false, AuthorIds: [friend, UserId]), 1, 20, Ct);
        Assert.Equal(new[] { parkingInLeeds.Id }, following.Items.Select(p => p.Id));
        Assert.Empty((await posts.SearchAsync(new PostSearch(null, null, null, false, AuthorIds: []), 1, 20, Ct)).Items);

        var parking = IncidentTypeFlags.From([IncidentCategory.BadParking]);
        var allParking = await posts.SearchAsync(new PostSearch(null, null, null, false, TypeFlags: parking), 1, 20, Ct);
        Assert.Equal(new[] { parkingInLeeds.Id, parkingInYork.Id }, allParking.Items.Select(p => p.Id));

        var parkingNearLeeds = await posts.SearchAsync(
            new PostSearch(null, null, GeoBox.Around(53.80m, -1.55m, 10), false, TypeFlags: parking), 1, 20, Ct);
        Assert.Equal(new[] { parkingInLeeds.Id }, parkingNearLeeds.Items.Select(p => p.Id));

        var timeline = Timeline.Create(UserId, new TimelineDetails("Parking", [IncidentCategory.BadParking]), 0, Now);
        context.Timelines.Add(timeline);
        await context.SaveChangesAsync(Ct);
        var saved = await new TimelineRepository(context).ListAsync(UserId, Ct);
        Assert.Equal(new[] { IncidentCategory.BadParking }, Assert.Single(saved).Types);
    }
}
