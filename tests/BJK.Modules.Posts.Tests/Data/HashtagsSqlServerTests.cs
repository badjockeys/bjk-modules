using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure;
using BJK.Modules.Posts.Infrastructure.Data;
using BJK.Modules.Posts.UseCases.Hashtags;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Data;

/// <summary>
/// Hashtags and Trending against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to run locally): links
/// saved with posts, suggestions, the tag feed, the hourly counts rebuild, and trending tags and towns, which ignore
/// hidden posts, need several authors and rank by growth. Uses a new database, dropped afterwards.
/// </summary>
public sealed class HashtagsSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_hashtags_test_" + Guid.NewGuid().ToString("N")[..12];
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

    /// <summary>Saves a post by <paramref name="author"/> at <paramref name="at"/>, linking its tags as the app does.</summary>
    private async Task<Post> PostAsync(Guid author, string title, DateTimeOffset at, string place = "A57 Warrington Road, Prescot")
    {
        await using var db = Context();
        var post = Post.Create(author, Details(title) with { Description = null, PlaceName = place }, [Guid.NewGuid()], at);
        db.Posts.Add(post);
        await new HashtagSync(new HashtagRepository(db), Options.Create(new HashtagOptions()), new FixedTimeProvider(at)).SyncAsync(post, Ct);
        await db.SaveChangesAsync(Ct);
        return post;
    }

    [Fact]
    public async Task Posts_link_their_tags_for_suggestions_and_the_tag_feed()
    {
        var first = await PostAsync(UserId, "Lorry on the #M62", Now.AddHours(-3));
        await PostAsync(OtherUserId, "Queue on the #m62 and #M6", Now.AddHours(-2));
        await PostAsync(OtherUserId, "Nothing tagged here", Now.AddHours(-1));

        await using var db = Context();
        var hashtags = new HashtagRepository(db);
        var suggestions = await hashtags.SuggestAsync("m6", 8, Ct);
        Assert.Equal(new[] { ("M62", 2), ("M6", 1) }, suggestions.Select(h => (h.Display, h.TotalUses)));

        var m62 = await hashtags.FindAsync("m62", Ct);
        var (items, total) = await new PostRepository(db).SearchAsync(
            new PostSearch(null, null, null, IncludeHidden: false, HashtagId: m62!.Id), 1, 20, Ct);
        Assert.Equal(2, total);
        Assert.Contains(items, p => p.Id == first.Id);
    }

    [Fact]
    public async Task Hourly_counts_are_rebuilt_from_public_posts_and_trending_needs_several_authors()
    {
        var authors = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();

        // #M62 is busy today from four people; #Leeds is busy but from one person; #A57 was as busy last week.
        foreach (var (author, i) in authors.Select((a, i) => (a, i)))
        {
            await PostAsync(author, "Queue on the #M62", Now.AddHours(-1 - i));
            await PostAsync(authors[0], "Again in #Leeds", Now.AddHours(-1 - i), "Kirkstall Road, Leeds");
            await PostAsync(author, "Lights out on the #A57", Now.AddHours(-2 - i));
            await PostAsync(author, "Lights out on the #A57", Now.AddDays(-3).AddHours(-i));
        }

        // A hidden post never counts.
        var hidden = await PostAsync(Guid.NewGuid(), "Hidden #M62", Now.AddHours(-1));
        await using (var db = Context())
        {
            var post = await db.Posts.SingleAsync(p => p.Id == hidden.Id, Ct);
            post.Hide("Spam", Now);
            await db.SaveChangesAsync(Ct);
        }

        await using var context = Context();
        var hashtags = new HashtagRepository(context);
        await hashtags.RebuildHourlyCountsAsync(HashtagMaintenance.RebuildFrom(Now, TimeSpan.FromDays(10)), Now.AddDays(-90), Ct);
        var hours = await context.HashtagHourlyCounts.ToListAsync(Ct);
        Assert.All(hours, h => Assert.Equal(0, h.Hour.Minute));
        Assert.Equal(4 + 4 + 4 + 4, hours.Sum(h => h.Posts));

        var dayAgo = Now.AddHours(-24);
        var tags = await hashtags.TrendingTagsAsync(dayAgo, dayAgo.AddDays(-7), 7, 3, 3, 10, Ct);
        Assert.Equal(new[] { ("M62", 4, 4, 0), ("A57", 4, 4, 4) }, tags.Select(t => (t.Name, t.Posts, t.Authors, t.BaselinePosts)));

        var places = await hashtags.TrendingPlacesAsync(dayAgo, dayAgo.AddDays(-7), 7, 3, 3, 5, Ct);
        Assert.Equal(new[] { ("Prescot", 8, 4) }, places.Select(p => (p.Name, p.Posts, p.Authors)));

        // Hidden by a moderator: off Trending and out of suggestions.
        var m62 = (await hashtags.GetByKeysAsync(["m62"], Ct)).Single();
        m62.SetHiddenFromTrends(true);
        await hashtags.SaveChangesAsync(Ct);
        Assert.DoesNotContain(await hashtags.TrendingTagsAsync(dayAgo, dayAgo.AddDays(-7), 7, 3, 3, 10, Ct), t => t.Name == "M62");
        Assert.Empty(await hashtags.SuggestAsync("m6", 8, Ct));
    }
}
