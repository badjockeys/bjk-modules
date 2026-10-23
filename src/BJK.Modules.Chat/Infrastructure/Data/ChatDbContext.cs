using System.Data;
using System.Data.Common;
using BJK.Modules.Chat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BJK.Modules.Chat.Infrastructure.Data;

/// <summary>The Chat module's table, in the <see cref="ChatModule.Schema"/> schema of the app database.</summary>
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<DailyUsage> DailyUsage => Set<DailyUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(ChatModule.Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChatDbContext).Assembly);
    }
}

internal sealed class DailyUsageConfiguration : IEntityTypeConfiguration<DailyUsage>
{
    public void Configure(EntityTypeBuilder<DailyUsage> builder)
    {
        builder.ToTable("DailyUsage");
        builder.HasKey(u => new { u.UserId, u.Day });
    }
}

internal sealed class ChatDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlServer(
                "Server=(design-time);Database=BJK_Design",
                sql => sql.MigrationsHistoryTable(ChatModule.MigrationsHistoryTable, ChatModule.Schema))
            .Options);
}

/// <summary>A member's counts for one UK day.</summary>
/// <param name="Questions">Questions answered (the allowance used).</param>
/// <param name="Strikes">Questions blocked as misuse.</param>
public sealed record DailyCounts(int Questions, int Strikes)
{
    public static readonly DailyCounts None = new(0, 0);
}

/// <summary>Each member's questions and strikes per UK day.</summary>
public interface IChatUsage
{
    /// <summary>Questions and strikes on <paramref name="day"/>.</summary>
    Task<DailyCounts> GetAsync(Guid userId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Records a blocked question (a strike), atomically. Returns the day's strikes afterwards.</summary>
    Task<int> AddStrikeAsync(Guid userId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>
    /// Counts one question if the member is under <paramref name="limit"/> for the day, atomically (two windows asking
    /// at once can't both take the last one). Returns the count afterwards, or null when the limit was already reached.
    /// </summary>
    Task<int?> TryTakeAsync(Guid userId, DateOnly day, int limit, CancellationToken cancellationToken);

    /// <summary>Gives a question back when it couldn't be answered for reasons that aren't the member's (the model was down).</summary>
    Task GiveBackAsync(Guid userId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Deletes every row for a member (account deleted).</summary>
    Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken);
}

internal sealed class ChatUsage(ChatDbContext db) : IChatUsage
{
    public async Task<DailyCounts> GetAsync(Guid userId, DateOnly day, CancellationToken cancellationToken) =>
        await db.DailyUsage.Where(u => u.UserId == userId && u.Day == day)
            .Select(u => new DailyCounts(u.Questions, u.Strikes))
            .FirstOrDefaultAsync(cancellationToken) ?? DailyCounts.None;

    public async Task<int> AddStrikeAsync(Guid userId, DateOnly day, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM chat.DailyUsage WHERE UserId = @UserId AND [Day] < @Day;
            IF NOT EXISTS (SELECT 1 FROM chat.DailyUsage WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId AND [Day] = @Day)
                INSERT INTO chat.DailyUsage (UserId, [Day], Questions, Strikes) VALUES (@UserId, @Day, 0, 1);
            ELSE
                UPDATE chat.DailyUsage SET Strikes = Strikes + 1 WHERE UserId = @UserId AND [Day] = @Day;
            COMMIT;
            SELECT Strikes FROM chat.DailyUsage WHERE UserId = @UserId AND [Day] = @Day;
            """;
        return (int)(await ScalarAsync(sql, userId, day, 0, cancellationToken))!;
    }

    public async Task<int?> TryTakeAsync(Guid userId, DateOnly day, int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DELETE FROM chat.DailyUsage WHERE UserId = @UserId AND [Day] < @Day;
            DECLARE @Taken int = NULL;
            -- The range lock makes two windows asking at once take turns, even for the day's first question.
            IF NOT EXISTS (SELECT 1 FROM chat.DailyUsage WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @UserId AND [Day] = @Day)
            BEGIN
                INSERT INTO chat.DailyUsage (UserId, [Day], Questions, Strikes) VALUES (@UserId, @Day, 1, 0);
                SET @Taken = 1;
            END
            ELSE
                UPDATE chat.DailyUsage
                    SET Questions = Questions + 1, @Taken = Questions + 1
                    WHERE UserId = @UserId AND [Day] = @Day AND Questions < @Limit;
            COMMIT;
            SELECT @Taken;
            """;
        var result = await ScalarAsync(sql, userId, day, limit, cancellationToken);
        return result is int taken ? taken : null;
    }

    public Task GiveBackAsync(Guid userId, DateOnly day, CancellationToken cancellationToken) =>
        db.DailyUsage.Where(u => u.UserId == userId && u.Day == day && u.Questions > 0)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Questions, u => u.Questions - 1), cancellationToken);

    public Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.DailyUsage.Where(u => u.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    private async Task<object?> ScalarAsync(string sql, Guid userId, DateOnly day, int limit, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            Add(command, "@UserId", DbType.Guid, userId);
            Add(command, "@Day", DbType.Date, day.ToDateTime(TimeOnly.MinValue));
            Add(command, "@Limit", DbType.Int32, limit);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is DBNull ? null : value;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
