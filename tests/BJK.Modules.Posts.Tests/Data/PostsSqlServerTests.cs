using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Data;

/// <summary>
/// Against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to run locally): the migrations apply, and
/// posts waiting for their media stay out of the feed but are in their author's list. Uses a new database, dropped
/// afterwards.
/// </summary>
public sealed class PostsSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_posts_test_" + Guid.NewGuid().ToString("N")[..12];
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
            .UseSqlServer(_connectionString, sql => sql.MigrationsHistoryTable(PostsModule.MigrationsHistoryTable, PostsModule.Schema))
            .Options);
    }

    [Fact]
    public async Task Pending_posts_are_only_in_their_authors_list_until_their_media_are_ready()
    {
        var mediaId = Guid.NewGuid();
        var pending = Post.Create(UserId, Details("Waiting for its video"), [mediaId], Now, mediaPending: true, notifyEmail: "roadhog@example.com");
        var live = Post.Create(UserId, Details("Already live"), [Guid.NewGuid()], Now.AddMinutes(-5));
        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            repository.Add(pending);
            repository.Add(live);
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            var (feed, _) = await repository.SearchAsync(new PostSearch(null, null, null, IncludeHidden: false), 1, 20, Ct);
            Assert.Equal(new[] { live.Id }, feed.Select(p => p.Id));

            var (mine, _) = await repository.SearchAsync(new PostSearch(UserId, null, null, IncludeHidden: true), 1, 20, Ct);
            Assert.Equal(2, mine.Count);

            var found = await repository.FindByMediaAsync(mediaId, Ct);
            Assert.NotNull(found);
            Assert.Equal(PostMediaState.Pending, found.MediaState);
            Assert.Equal("roadhog@example.com", found.NotifyEmail);
            Assert.True(found.MarkMediaReady(Now));
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var (feed, _) = await new PostRepository(db).SearchAsync(new PostSearch(null, null, null, IncludeHidden: false), 1, 20, Ct);
            Assert.Equal(2, feed.Count);
            Assert.Null(feed.Single(p => p.Id == pending.Id).NotifyEmail);
        }
    }

    [Fact]
    public async Task The_feed_filters_by_any_of_a_posts_types()
    {
        var both = Post.Create(UserId, Details("Tailgated then shouted at") with { Types = [IncidentCategory.Tailgating, IncidentCategory.RoadRage] }, [Guid.NewGuid()], Now);
        var parking = Post.Create(UserId, Details("Across two bays") with { Category = IncidentCategory.BadParking }, [Guid.NewGuid()], Now);
        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            repository.Add(both);
            repository.Add(parking);
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            var (rage, _) = await repository.SearchAsync(new PostSearch(null, IncidentCategory.RoadRage, null, IncludeHidden: false), 1, 20, Ct);
            Assert.Equal(new[] { both.Id }, rage.Select(p => p.Id));
            Assert.Equal(new[] { IncidentCategory.Tailgating, IncidentCategory.RoadRage }, rage[0].Types);

            var (parked, _) = await repository.SearchAsync(new PostSearch(null, IncidentCategory.BadParking, null, IncludeHidden: false), 1, 20, Ct);
            Assert.Equal(new[] { parking.Id }, parked.Select(p => p.Id));
        }
    }

    [Fact]
    public async Task Passenger_tags_are_listed_for_the_tagged_person_and_go_with_the_post()
    {
        var post = Post.Create(UserId, Details("Cut up at the roundabout"), [Guid.NewGuid()], Now);
        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            repository.Add(post);
            repository.AddPassenger(PostPassenger.Tag(post.Id, OtherUserId, Now));
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            var (pending, total) = await repository.ListTaggedAsync(OtherUserId, PassengerTagStatus.Pending, 1, 20, Ct);
            Assert.Equal(1, total);
            Assert.Equal(post.Id, Assert.Single(pending).Id);

            var tag = Assert.Single(await repository.GetPassengersAsync([post.Id], Ct));
            Assert.True(tag.Respond(accept: true, Now));
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var repository = new PostRepository(db);
            Assert.Empty((await repository.ListTaggedAsync(OtherUserId, PassengerTagStatus.Pending, 1, 20, Ct)).Items);
            Assert.Single((await repository.ListTaggedAsync(OtherUserId, PassengerTagStatus.Accepted, 1, 20, Ct)).Items);

            // Deleting the author's account deletes the post and its tags.
            await repository.DeleteAllForUserAsync(UserId, Ct);
            Assert.Empty(await repository.GetPassengersAsync([post.Id], Ct));
        }
    }
}
