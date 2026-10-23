using BJK.Modules.Chat.Sql;
using Xunit;

namespace BJK.Modules.Chat.Tests;

public sealed class SmallCountsTests
{
    /// <summary>A result whose last column is the incident count and whose other numeric columns after the first are measures.</summary>
    internal static QueryResult Result(string[] columns, params object?[][] rows)
    {
        var count = columns.Length - 1;
        var query = new CheckedQuery("SELECT 1", count, [count], true);
        return SmallCounts.Hide(columns, rows.ToList(), query, false, TimeSpan.Zero);
    }

    [Fact]
    public void Blanks_the_figures_on_rows_with_one_or_two_incidents()
    {
        var query = new CheckedQuery("SELECT 1", 1, [1, 2], true);
        List<object?[]> rows =
        [
            ["Leeds", 12, 340.5m],
            ["Huyton", 2, 15.0m],
            ["Prescot", 1L, 9.0m],
            ["Nowhere", 0, null],
            ["Bootle", 3, 21.0m],
        ];

        var result = SmallCounts.Hide(["Town", "Incidents", "AvgViews"], rows, query, false, TimeSpan.Zero);

        Assert.Equal(new[] { false, true, true, false, false }, result.Hidden);
        Assert.Equal(2, result.HiddenRows);
        Assert.Equal(new object?[] { "Huyton", null, null }, result.Rows[1]);
        Assert.Equal(new object?[] { "Prescot", null, null }, result.Rows[2]);
        Assert.Equal(new object?[] { "Leeds", 12, 340.5m }, result.Rows[0]);
        Assert.Equal(new object?[] { "Nowhere", 0, null }, result.Rows[3]);
    }

    [Theory]
    [InlineData((byte)2)]
    [InlineData((short)2)]
    [InlineData(2)]
    [InlineData(2L)]
    [InlineData(2.0)]
    public void Reads_counts_of_any_numeric_type(object count)
    {
        var result = Result(["Incidents"], [count]);

        Assert.True(result.Hidden[0]);
    }

    [Fact]
    public void Decimal_counts_count_too()
    {
        var result = Result(["Incidents"], [2m]);

        Assert.True(result.Hidden[0]);
    }
}
