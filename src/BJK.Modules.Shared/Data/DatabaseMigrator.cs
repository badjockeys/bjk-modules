using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BJK.Modules.Shared.Data;

/// <summary>
/// Applies pending EF Core migrations at startup, refusing any migration that would drop data
/// unless <see cref="AllowDestructiveSetting"/> is explicitly set to true.
/// </summary>
public static class DatabaseMigrator
{
    public const string AllowDestructiveSetting = "Database:AllowDestructiveMigrations";

    /// <summary>
    /// Creates the database if needed and applies pending migrations for <typeparamref name="TContext"/>.
    /// Existing data is never removed: a pending migration that drops a table, column or schema,
    /// or deletes rows, stops startup with an error listing the offending operations.
    /// </summary>
    public static async Task MigrateDatabaseAsync<TContext>(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));
        var db = provider.GetRequiredService<TContext>();
        var contextName = typeof(TContext).Name;

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("{Context}: database schema is up to date.", contextName);
            return;
        }

        var destructive = FindDestructiveOperations(db, pending);
        if (destructive.Count > 0)
        {
            var allowDestructive = provider.GetRequiredService<IConfiguration>().GetValue<bool>(AllowDestructiveSetting);
            if (!allowDestructive)
            {
                throw new InvalidOperationException(
                    $"{contextName}: pending migrations would remove data, so they were not applied. "
                    + $"Review them, then set '{AllowDestructiveSetting}' to true for one deployment to allow it.{Environment.NewLine}"
                    + string.Join(Environment.NewLine, destructive.Select(d => "  - " + d)));
            }

            logger.LogWarning(
                "{Context}: applying destructive migrations because {Setting} is true: {Operations}",
                contextName,
                AllowDestructiveSetting,
                destructive);
        }

        logger.LogInformation("{Context}: applying migrations {Migrations}.", contextName, pending);
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("{Context}: migrations applied.", contextName);
    }

    private static List<string> FindDestructiveOperations(DbContext db, IReadOnlyList<string> pendingMigrationIds)
    {
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var providerName = db.Database.ProviderName
            ?? throw new InvalidOperationException("The database provider is not configured.");

        var found = new List<string>();
        foreach (var id in pendingMigrationIds)
        {
            if (!migrationsAssembly.Migrations.TryGetValue(id, out var migrationType))
            {
                continue;
            }

            var migration = migrationsAssembly.CreateMigration(migrationType, providerName);
            found.AddRange(migration.UpOperations
                .Select(Describe)
                .OfType<string>()
                .Select(description => $"{id}: {description}"));
        }

        return found;
    }

    private static string? Describe(MigrationOperation operation) => operation switch
    {
        DropTableOperation t => $"drops table {Qualified(t.Schema, t.Name)}",
        DropColumnOperation c => $"drops column {Qualified(c.Schema, c.Table)}.{c.Name}",
        DropSchemaOperation s => $"drops schema {s.Name}",
        DeleteDataOperation d => $"deletes rows from {Qualified(d.Schema, d.Table)}",
        _ => null,
    };

    private static string Qualified(string? schema, string name) =>
        string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
}
