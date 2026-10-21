using BJK.Modules.Posts.Domain;
using BJK.Modules.Posts.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BJK.Modules.Posts.Tests.TestData;

namespace BJK.Modules.Posts.Tests.Data;

/// <summary>
/// Votes, follows, bookmarks, views and shares against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c>
/// to run locally): the hand-written inserts count each viewer once, and account deletion clears it all.
/// </summary>
public sealed class EngagementSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_engagement_test_" + Guid.NewGuid().ToString("N")[..12];
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

    private async Task<Post> SavedPostAsync(Guid authorId, VoteDuration duration = VoteDuration.Days7)
    {
        var post = NewPost(authorId);
        post.SetVoteDuration(duration);
        await using var db = Context();
        var repository = new PostRepository(db);
        repository.Add(post);
        await repository.SaveChangesAsync(Ct);
        return post;
    }

    [Fact]
    public async Task Views_and_shares_count_each_viewer_once()
    {
        var post = await SavedPostAsync(UserId);
        var viewer = Guid.NewGuid();
        await using (var db = Context())
        {
            var engagement = new EngagementRepository(db);
            Assert.Equal(2, await engagement.RecordViewsAsync(
                [new ViewToRecord(post.Id, viewer, true), new ViewToRecord(post.Id, OtherUserId, false), new ViewToRecord(Guid.NewGuid(), viewer, false)],
                Now,
                Ct));
            Assert.Equal(0, await engagement.RecordViewsAsync([new ViewToRecord(post.Id, viewer, true)], Now.AddHours(1), Ct));

            var today = DateOnly.FromDateTime(Now.UtcDateTime);
            Assert.True(await engagement.RecordShareAsync(PostShare.Create(post.Id, viewer, today, Now), Ct));
            Assert.False(await engagement.RecordShareAsync(PostShare.Create(post.Id, viewer, today, Now.AddMinutes(5)), Ct));
            Assert.True(await engagement.RecordShareAsync(PostShare.Create(post.Id, viewer, today.AddDays(1), Now.AddDays(1)), Ct));

            Assert.Equal(new PostReach(Views: 2, LocalViews: 1, Shares: 2), await engagement.GetReachAsync(post.Id, Ct));
        }
    }

    [Fact]
    public async Task Following_is_safe_to_repeat_and_followers_come_oldest_first()
    {
        var post = await SavedPostAsync(UserId);
        await using var db = Context();
        var engagement = new EngagementRepository(db);

        await engagement.EnsureFollowingAsync(post.Id, UserId, Now, Ct);
        await engagement.EnsureFollowingAsync(post.Id, OtherUserId, Now.AddMinutes(1), Ct);
        await engagement.EnsureFollowingAsync(post.Id, UserId, Now.AddMinutes(2), Ct);

        Assert.Equal([UserId, OtherUserId], await engagement.ListFollowerIdsAsync(post.Id, 10, Ct));
        Assert.Equal([UserId], await engagement.ListFollowerIdsAsync(post.Id, 1, Ct));
    }

    [Fact]
    public async Task Tallies_count_each_answer_and_know_yours()
    {
        var post = await SavedPostAsync(UserId);
        await using (var db = Context())
        {
            var engagement = new EngagementRepository(db);
            engagement.AddVote(PostVote.Create(post.Id, OtherUserId, VoteChoice.OtherRoadUser, Now));
            engagement.AddVote(PostVote.Create(post.Id, Guid.NewGuid(), VoteChoice.OtherRoadUser, Now));
            engagement.AddVote(PostVote.Create(post.Id, Guid.NewGuid(), VoteChoice.Both, Now));
            await engagement.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var tally = (await new EngagementRepository(db).GetVoteTalliesAsync([post.Id], OtherUserId, Ct))[post.Id];

            Assert.Equal(3, tally.Total);
            Assert.Equal(2, tally.Counts[VoteChoice.OtherRoadUser]);
            Assert.Equal(VoteChoice.OtherRoadUser, tally.Mine);
        }
    }

    [Fact]
    public async Task Closed_votes_are_listed_until_announced()
    {
        var closing = await SavedPostAsync(UserId, VoteDuration.Hours48);
        await SavedPostAsync(UserId, VoteDuration.Never);
        var later = Now.AddDays(3);

        await using (var db = Context())
        {
            var engagement = new EngagementRepository(db);
            var due = await engagement.ListVotesToAnnounceAsync(later, 10, Ct);
            Assert.Equal([closing.Id], due.Select(p => p.Id));

            due[0].MarkVoteResultSent(later);
            await engagement.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            Assert.Empty(await new EngagementRepository(db).ListVotesToAnnounceAsync(later, 10, Ct));
        }
    }

    [Fact]
    public async Task Bookmarks_list_newest_first_and_leave_out_hidden_posts()
    {
        var first = await SavedPostAsync(UserId);
        var second = await SavedPostAsync(UserId);
        var hidden = NewPost(UserId);
        await using (var db = Context())
        {
            hidden.Hide("Test", Now);
            db.Posts.Add(hidden);
            var engagement = new EngagementRepository(db);
            engagement.AddBookmark(Bookmark.Create(OtherUserId, first.Id, Now));
            engagement.AddBookmark(Bookmark.Create(OtherUserId, second.Id, Now.AddMinutes(1)));
            engagement.AddBookmark(Bookmark.Create(OtherUserId, hidden.Id, Now.AddMinutes(2)));
            await engagement.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var (items, total) = await new EngagementRepository(db).ListBookmarkedAsync(OtherUserId, 1, 20, Ct);

            Assert.Equal(2, total);
            Assert.Equal([second.Id, first.Id], items.Select(p => p.Id));
        }
    }

    [Fact]
    public async Task Deleting_an_account_removes_its_votes_follows_bookmarks_views_and_settings()
    {
        var post = await SavedPostAsync(UserId);
        await using (var db = Context())
        {
            var engagement = new EngagementRepository(db);
            engagement.AddVote(PostVote.Create(post.Id, OtherUserId, VoteChoice.Both, Now));
            engagement.AddFollow(PostFollow.Create(post.Id, OtherUserId, Now));
            engagement.AddBookmark(Bookmark.Create(OtherUserId, post.Id, Now));
            var preferences = AuthorPreferences.Create(OtherUserId, Now);
            preferences.SetVoteDuration(VoteDuration.Never, Now);
            engagement.AddPreferences(preferences);
            await engagement.SaveChangesAsync(Ct);
            await engagement.RecordViewsAsync([new ViewToRecord(post.Id, OtherUserId, false)], Now, Ct);
        }

        await using (var db = Context())
        {
            await new PostRepository(db).DeleteAllForUserAsync(OtherUserId, Ct);
        }

        await using (var db = Context())
        {
            Assert.False(await db.PostVotes.AnyAsync(v => v.UserId == OtherUserId, Ct));
            Assert.False(await db.PostFollows.AnyAsync(f => f.UserId == OtherUserId, Ct));
            Assert.False(await db.Bookmarks.AnyAsync(b => b.UserId == OtherUserId, Ct));
            Assert.False(await db.PostViews.AnyAsync(v => v.ViewerKey == OtherUserId, Ct));
            Assert.False(await db.AuthorPreferences.AnyAsync(a => a.UserId == OtherUserId, Ct));
            Assert.True(await db.Posts.AnyAsync(p => p.Id == post.Id, Ct));
        }
    }
}
