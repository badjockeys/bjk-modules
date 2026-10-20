using BJK.Modules.Notifications.Domain;
using BJK.Modules.Notifications.Infrastructure.Data;
using BJK.Modules.Shared.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
using static BJK.Modules.Notifications.Tests.TestData;

namespace BJK.Modules.Notifications.Tests.Data;

public sealed class NotificationsMigrationsTests
{
    [Fact]
    public void Migrations_never_drop_or_delete_data_and_use_the_notifications_schema()
    {
        using var db = new NotificationsDbContextDesignTimeFactory().CreateDbContext([]);
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();

        Assert.NotEmpty(migrationsAssembly.Migrations);
        var destructive = migrationsAssembly.Migrations
            .Select(m => migrationsAssembly.CreateMigration(m.Value, db.Database.ProviderName!))
            .SelectMany(m => m.UpOperations)
            .Where(op => op is DropTableOperation or DropColumnOperation or DropSchemaOperation or DeleteDataOperation)
            .ToList();
        Assert.Empty(destructive);
        Assert.Equal(NotificationsModule.Schema, db.Model.FindEntityType(typeof(Notification))!.GetSchema());
    }
}

/// <summary>Against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to run locally).</summary>
public sealed class NotificationsSqlServerTests : IAsyncLifetime
{
    private readonly string _database = "bjk_notifications_test_" + Guid.NewGuid().ToString("N")[..12];
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

    private NotificationsDbContext Context()
    {
        if (_connectionString is null)
        {
            Assert.Skip("BJK_TEST_SQLSERVER isn't set.");
        }

        return new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseSqlServer(_connectionString, sql => sql.MigrationsHistoryTable(NotificationsModule.MigrationsHistoryTable, NotificationsModule.Schema))
            .Options);
    }

    [Fact]
    public async Task The_bell_lists_newest_first_counts_unread_and_marks_read()
    {
        await using (var db = Context())
        {
            var repository = new NotificationRepository(db);
            repository.Add(Notification.Create(new NotificationRequest(UserId, NotificationType.CommentOnPost, SamId, PostId), Now.AddMinutes(-10)));
            repository.Add(Notification.Create(new NotificationRequest(UserId, NotificationType.ReactionOnPost, SamId, PostId), Now));
            repository.Add(Notification.Create(new NotificationRequest(SamId, NotificationType.CommentOnPost, UserId, PostId), Now));
            repository.Add(Notification.Create(new NotificationRequest(UserId, NotificationType.ReplyToComment, SamId, PostId), Now.AddDays(-100)));
            await repository.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var repository = new NotificationRepository(db);
            var (items, total) = await repository.ListAsync(UserId, 1, 20, Ct);
            Assert.Equal(3, total);
            Assert.Equal(NotificationType.ReactionOnPost, items[0].Type);
            Assert.Equal(3, await repository.CountUnreadAsync(UserId, Ct));
            Assert.NotNull(await repository.FindUnreadAsync(UserId, NotificationType.ReactionOnPost, PostId, Ct));

            await repository.MarkReadAsync(UserId, [items[0].Id], Now, Ct);
            Assert.Equal(2, await repository.CountUnreadAsync(UserId, Ct));
            await repository.MarkReadAsync(UserId, null, Now, Ct);
            Assert.Equal(0, await repository.CountUnreadAsync(UserId, Ct));
            Assert.Equal(1, await repository.CountUnreadAsync(SamId, Ct));

            Assert.Equal(1, await repository.DeleteOlderThanAsync(Now - Notification.KeepFor, Ct));
            await repository.DeleteAllForUserAsync(UserId, Ct);
            Assert.Equal(0, (await repository.ListAsync(UserId, 1, 20, Ct)).TotalCount);
        }
    }

    [Fact]
    public async Task Subscribers_around_a_post_are_found_and_their_held_posts_listed()
    {
        await using (var db = Context())
        {
            var areas = new AreaAlertRepository(db);
            var near = AreaSubscription.Create(UserId, "Rainhill", 53.42m, -2.76m, Now);
            near.RecordPlan(true, Now);
            near.SetAlerts(true, 5, AreaAlertMode.Digest, false, Now);
            var far = AreaSubscription.Create(SamId, "Leeds", 53.80m, -1.55m, Now);
            far.RecordPlan(true, Now);
            far.SetAlerts(true, 25, AreaAlertMode.Digest, false, Now);
            areas.Add(near);
            areas.Add(far);
            areas.AddItem(AreaAlertItem.Create(UserId, PostId, "Tailgated", "Rainhill", Now));
            await areas.SaveChangesAsync(Ct);
        }

        await using (var db = Context())
        {
            var areas = new AreaAlertRepository(db);
            var found = await areas.FindAroundAsync(53.43m, -2.77m, 25, Ct);
            Assert.Equal(UserId, Assert.Single(found).UserId);
            Assert.Equal(UserId, Assert.Single(await areas.ListWithWaitingAsync(Ct)).UserId);
            Assert.Single(await areas.GetWaitingAsync(UserId, Now.AddMinutes(1), Ct));
            Assert.Empty(await areas.GetWaitingAsync(UserId, Now, Ct));

            await areas.RemoveAsync(UserId, Ct);
            Assert.Null(await areas.GetAsync(UserId, Ct));
            Assert.Empty(await areas.ListWithWaitingAsync(Ct));
        }
    }
}
