using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Sql;
using Xunit;

namespace BJK.Modules.Chat.Tests;

public sealed class SqlGuardTests
{
    public static TheoryData<string> WorkedExamples()
    {
        var data = new TheoryData<string>();
        foreach (var example in AgentPrompt.Examples)
        {
            data.Add(TestSql.Fill(example.Sql));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public void Every_worked_example_passes(string sql)
    {
        var check = SqlGuard.Check(sql);

        Assert.Null(check.Problem);
        Assert.NotNull(check.Query);
    }

    [Fact]
    public void Adds_the_plan_date_limit_and_keeps_the_alias()
    {
        var check = SqlGuard.Check("SELECT i.PlaceName, COUNT(*) AS Incidents FROM reporting.Incidents AS i GROUP BY i.PlaceName");

        var sql = check.Query!.Sql;
        Assert.Contains("(SELECT * FROM reporting.Incidents WHERE IncidentDate >= @HistoryFrom) AS [i]", sql);
        Assert.DoesNotContain("reporting.Incidents AS i", sql);
        Assert.True(check.Query.ReadsIncidents);
    }

    [Fact]
    public void Without_an_alias_the_view_keeps_its_name()
    {
        var check = SqlGuard.Check("SELECT Incidents.HourUk, COUNT(*) AS n FROM [reporting].[Incidents] GROUP BY Incidents.HourUk");

        Assert.Contains("WHERE IncidentDate >= @HistoryFrom) AS [Incidents]", check.Query!.Sql);
    }

    [Fact]
    public void Adds_the_limit_inside_a_with_table_and_a_join()
    {
        var check = SqlGuard.Check(
            """
            WITH counts AS (SELECT Subcategory, COUNT(*) AS n FROM reporting.Incidents GROUP BY Subcategory)
            SELECT s.SubcategoryLabel, COUNT(*) AS Kinds, SUM(c.n) AS Incidents
            FROM reporting.Subcategories s LEFT JOIN counts c ON c.Subcategory = s.Subcategory
            GROUP BY s.SubcategoryLabel
            """);

        Assert.Null(check.Problem);
        Assert.Contains("FROM (SELECT * FROM reporting.Incidents WHERE IncidentDate >= @HistoryFrom) AS [Incidents] GROUP BY", check.Query!.Sql);
    }

    [Fact]
    public void Subcategories_alone_need_no_date_limit()
    {
        var check = SqlGuard.Check("SELECT GroupLabel, COUNT(*) AS Kinds FROM reporting.Subcategories GROUP BY GroupLabel");

        Assert.False(check.Query!.ReadsIncidents);
        Assert.DoesNotContain("@HistoryFrom", check.Query.Sql);
    }

    [Fact]
    public void Finds_the_count_and_measure_columns()
    {
        var check = SqlGuard.Check(
            "SELECT PlaceName AS Place, COUNT(DISTINCT IncidentDate) AS Days, COUNT(*) AS Incidents, AVG(Views) AS AvgViews, ROUND(100.0 * COUNT(*) / SUM(COUNT(*)) OVER (), 0) AS Pct FROM reporting.Incidents GROUP BY PlaceName");

        var query = check.Query!;
        Assert.Equal(2, query.IncidentCountColumn); // COUNT(*), not COUNT(DISTINCT ...)
        Assert.Equal(new[] { 1, 2, 3, 4 }, query.MeasureColumns);
    }

    [Fact]
    public void A_count_wrapped_in_cast_is_still_the_count()
    {
        var check = SqlGuard.Check("SELECT HourUk, CAST(COUNT(*) AS int) AS Incidents FROM reporting.Incidents GROUP BY HourUk");

        Assert.Equal(1, check.Query!.IncidentCountColumn);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("SELEC COUNT(*) FROM reporting.Incidents", "valid T-SQL")]
    [InlineData("DELETE FROM reporting.Incidents", "one SELECT")]
    [InlineData("UPDATE reporting.Incidents SET Views = 0", "one SELECT")]
    [InlineData("EXEC sp_who", "one SELECT")]
    [InlineData("WAITFOR DELAY '00:00:10'", "one SELECT")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents; SELECT COUNT(*) AS n FROM reporting.Incidents", "one SELECT")]
    [InlineData("SELECT COUNT(*) AS n INTO #t FROM reporting.Incidents", "INTO")]
    [InlineData("SELECT COUNT(*) AS n FROM sys.objects", "can be read")]
    [InlineData("SELECT COUNT(*) AS n FROM posts.Posts", "can be read")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Reports", "can be read")]
    [InlineData("SELECT COUNT(*) AS n FROM BadJockeys.reporting.Incidents", "can be read")]
    [InlineData("SELECT COUNT(*) AS n FROM Incidents", "reporting. schema")]
    [InlineData("SELECT COUNT(*) AS n FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1')", "FROM")]
    [InlineData("SELECT COUNT(*) AS n FROM (VALUES (1), (2), (3)) v(x)", "FROM")]
    [InlineData("SELECT COUNT(*) AS n FROM STRING_SPLIT('a,b', ',')", "FROM")]
    [InlineData("SELECT PlaceName, IncidentDate FROM reporting.Incidents", "COUNT")]
    [InlineData("SELECT DISTINCT PlaceName FROM reporting.Incidents", "COUNT")]
    [InlineData("SELECT PlaceName, COUNT(*) OVER () AS n FROM reporting.Incidents", "COUNT")]
    [InlineData("SELECT * FROM reporting.Incidents", "SELECT *")]
    [InlineData("SELECT i.PlaceName, COUNT(*) AS n FROM reporting.Incidents i CROSS JOIN reporting.Subcategories s GROUP BY i.PlaceName", "CROSS JOIN")]
    [InlineData("SELECT i.PlaceName, COUNT(*) AS n FROM reporting.Incidents i CROSS APPLY (SELECT 1 AS x) a GROUP BY i.PlaceName", "CROSS JOIN")]
    [InlineData("SELECT i.PlaceName, COUNT(*) AS n FROM reporting.Incidents i, reporting.Subcategories s GROUP BY i.PlaceName", "Commas")]
    [InlineData("SELECT i.PlaceName, COUNT(*) AS n FROM reporting.Incidents i JOIN reporting.Subcategories s ON s.[Group] = i.[Group] GROUP BY i.PlaceName", "match on Subcategory")]
    [InlineData("SELECT i.PlaceName, COUNT(*) AS n FROM reporting.Incidents i JOIN reporting.Subcategories s ON s.Subcategory = i.Subcategory OR 1 = 1 GROUP BY i.PlaceName", "match on Subcategory")]
    [InlineData("SELECT a.PlaceName, COUNT(*) AS n FROM reporting.Incidents a JOIN reporting.Incidents b ON a.Subcategory = b.Subcategory GROUP BY a.PlaceName", "only once")]
    [InlineData("SELECT COUNT(*) AS n, (SELECT COUNT(*) FROM reporting.Incidents) AS total FROM reporting.Incidents", "only once")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents UNION ALL SELECT COUNT(*) FROM reporting.Subcategories", "UNION")]
    [InlineData("WITH x AS (SELECT Subcategory FROM reporting.Subcategories UNION ALL SELECT Subcategory FROM reporting.Subcategories) SELECT COUNT(*) AS n FROM x", "UNION")]
    [InlineData("WITH x AS (SELECT Subcategory FROM reporting.Subcategories) SELECT COUNT(*) AS n FROM x a JOIN x b ON a.Subcategory = b.Subcategory", "once")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents WHERE IncidentDate > @d", "Variables")]
    [InlineData("SELECT @@VERSION AS v, COUNT(*) AS n FROM reporting.Incidents", "@@")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents WITH (NOLOCK)", "hints")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents OPTION (MAXDOP 64)", "OPTION")]
    [InlineData("SELECT COUNT(*) AS n FROM reporting.Incidents FOR JSON PATH", "FOR JSON")]
    [InlineData("SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS p, COUNT(*) AS n FROM reporting.Incidents", "HAS_PERMS_BY_NAME")]
    [InlineData("SELECT OBJECT_ID('posts.Posts') AS o, COUNT(*) AS n FROM reporting.Incidents", "OBJECT_ID")]
    [InlineData("SELECT dbo.fn_secret() AS s, COUNT(*) AS n FROM reporting.Incidents", "built-in")]
    [InlineData("SELECT STRING_AGG(PlaceName, ', ') AS places, COUNT(*) AS n FROM reporting.Incidents", "STRING_AGG")]
    [InlineData("SELECT reporting.Incidents.PlaceName, COUNT(*) AS n FROM reporting.Incidents GROUP BY reporting.Incidents.PlaceName", "alias")]
    public void Refuses_what_isnt_allowed(string sql, string problemMentions)
    {
        var check = SqlGuard.Check(sql);

        Assert.Null(check.Query);
        Assert.Contains(problemMentions, check.Problem, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class TestSql
{
    /// <summary>Fills a worked example's placeholders with fixed values.</summary>
    public static string Fill(string sql) => sql
        .Replace("{TODAY}", "2026-10-23", StringComparison.Ordinal)
        .Replace("{THIS_MONTH}", "2026-10-01", StringComparison.Ordinal)
        .Replace("{LAST_12}", "2025-11-01", StringComparison.Ordinal)
        .Replace("{YEAR}", "2026", StringComparison.Ordinal)
        .Replace("{LAST_YEAR}", "2025", StringComparison.Ordinal)
        .Replace("{MONTH}", "10", StringComparison.Ordinal)
        .Replace("{DAY}", "23", StringComparison.Ordinal)
        .Replace("{NEAR}", AgentPrompt.NearFilter(new NearPlace(53.43, -2.80, "Prescot")), StringComparison.Ordinal);
}
