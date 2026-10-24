using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace BJK.Modules.Chat.Sql;

/// <summary>
/// One query's rows after the small-count rule: a row whose incident count is 1 or 2 has its count and other
/// aggregates blanked (null) and <see cref="Hidden"/> set for it.
/// </summary>
/// <param name="Columns">Column names, in order (unnamed ones become column1, column2...).</param>
/// <param name="Rows">The rows, at most <see cref="ChatOptions.MaxRows"/>.</param>
/// <param name="Hidden">Per row: true when its figures were blanked for having fewer than 3 incidents.</param>
/// <param name="Truncated">True when there were more rows than <see cref="ChatOptions.MaxRows"/>.</param>
/// <param name="IncidentCountColumn">Which column holds the incident count.</param>
/// <param name="Duration">How long the query took.</param>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<bool> Hidden,
    bool Truncated,
    int IncidentCountColumn,
    TimeSpan Duration)
{
    public int HiddenRows => Hidden.Count(h => h);
}

/// <summary>A query that ran (<see cref="Result"/>) or failed with a <see cref="Problem"/> the model can fix.</summary>
public sealed record QueryOutcome(QueryResult? Result, string? Problem);

/// <summary>Runs checked queries against the incident views through the bjk_chat login.</summary>
public interface IIncidentQueries
{
    /// <summary>Runs <paramref name="query"/> with the member's earliest date, then applies the small-count rule.</summary>
    Task<QueryOutcome> RunAsync(CheckedQuery query, DateOnly historyFrom, CancellationToken cancellationToken);
}

/// <summary>The bjk_chat login's connection string (<c>ConnectionStrings:BadJockeysChat</c>), or null when it isn't set.</summary>
internal sealed record ChatDatabase(string? ConnectionString);

internal sealed class SqlIncidentQueries(ChatDatabase database) : IIncidentQueries
{
    public async Task<QueryOutcome> RunAsync(CheckedQuery query, DateOnly historyFrom, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(database.ConnectionString))
        {
            throw new Shared.Exceptions.ServiceUnavailableException(
                "Ask AI isn't set up here: ConnectionStrings:BadJockeysChat is missing.");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = query.Sql;
            command.CommandTimeout = (int)ChatOptions.QueryTimeout.TotalSeconds;
            command.Parameters.Add(new SqlParameter(SqlGuard.HistoryParameter, SqlDbType.Date) { Value = historyFrom.ToDateTime(TimeOnly.MinValue) });

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                var name = reader.GetName(i);
                columns[i] = string.IsNullOrWhiteSpace(name) ? $"column{i + 1}" : name;
            }

            var dates = Enumerable.Range(0, columns.Length).Select(i => reader.GetDataTypeName(i) == "date").ToArray();
            var rows = new List<object?[]>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == ChatOptions.MaxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new object?[columns.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[i] = value is DateTime date && dates[i] ? DateOnly.FromDateTime(date) : value;
                }

                rows.Add(row);
            }

            watch.Stop();
            return new QueryOutcome(SmallCounts.Hide(columns, rows, query, truncated, watch.Elapsed), null);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            return new QueryOutcome(null, "The query took longer than 5 seconds. Group by fewer things or narrow the dates or area, then try again.");
        }
        catch (SqlException ex) when (ex.Number is 229 or 230 or 208)
        {
            return new QueryOutcome(null, $"{ex.Message} Only reporting.Incidents and reporting.Subcategories can be read.");
        }
        catch (SqlException ex) when (ex.Class < 17)
        {
            // A mistake in the query (bad column name, type mismatch and so on): the model can fix it.
            return new QueryOutcome(null, ex.Message);
        }
    }
}

/// <summary>The "fewer than 3" rule: blanks the figures on rows that count 1 or 2 incidents.</summary>
internal static class SmallCounts
{
    public static QueryResult Hide(IReadOnlyList<string> columns, List<object?[]> rows, CheckedQuery query, bool truncated, TimeSpan duration)
    {
        var hidden = new bool[rows.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (AsNumber(row[query.IncidentCountColumn]) is { } count && count > 0 && count < ChatOptions.HideCountsUnder)
            {
                hidden[r] = true;
                foreach (var column in query.MeasureColumns.Append(query.IncidentCountColumn))
                {
                    row[column] = null;
                }
            }
        }

        return new QueryResult(columns, rows, hidden, truncated, query.IncidentCountColumn, duration);
    }

    /// <summary>A numeric cell as a double, or null for anything else.</summary>
    public static double? AsNumber(object? value) => value switch
    {
        null => null,
        byte b => b,
        short s => s,
        int i => i,
        long l => l,
        decimal d => (double)d,
        double d => d,
        float f => f,
        _ => null,
    };
}
