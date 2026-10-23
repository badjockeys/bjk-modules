using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace BJK.Modules.Chat.Sql;

/// <summary>
/// A query that passed <see cref="SqlGuard"/>: rewritten so <c>reporting.Incidents</c> only shows incidents on or
/// after <see cref="SqlGuard.HistoryParameter"/>, plus which result columns hold counts and other aggregates.
/// </summary>
/// <param name="Sql">The SQL to run, with the date limit added.</param>
/// <param name="IncidentCountColumn">The result column (0-based) whose value decides whether a row is hidden.</param>
/// <param name="MeasureColumns">Result columns built from aggregates (counts, sums, averages, minimums...): blanked on hidden rows.</param>
/// <param name="ReadsIncidents">True when the query reads <c>reporting.Incidents</c>.</param>
public sealed record CheckedQuery(string Sql, int IncidentCountColumn, IReadOnlyList<int> MeasureColumns, bool ReadsIncidents);

/// <summary>The outcome of <see cref="SqlGuard.Check"/>: a <see cref="Query"/>, or the <see cref="Problem"/> to show the model.</summary>
internal sealed record SqlCheck(CheckedQuery? Query, string? Problem)
{
    public static SqlCheck Fail(string problem) => new(null, problem);
}

/// <summary>
/// Checks the SQL the model writes before anything runs it, with Microsoft's T-SQL parser. The bjk_chat login can
/// only read the two incident views anyway; these checks add what the database can't:
/// <list type="bullet">
/// <item>exactly one SELECT, no INTO, variables, hints, FOR XML/JSON or functions outside a short list;</item>
/// <item>only <c>reporting.Incidents</c> and <c>reporting.Subcategories</c> (and WITH tables built from them);</item>
/// <item>answers are grouped counts: the outer SELECT must include COUNT(...), so no query lists single incidents;</item>
/// <item>nothing that repeats incident rows (CROSS JOIN, APPLY, UNION, VALUES, reading the incidents twice, or a join
/// that isn't on Subcategory), so a count really is a number of incidents and the "fewer than 3" rule can't be
/// dodged;</item>
/// <item>the member's plan date limit, added to every read of the incidents.</item>
/// </list>
/// Problems come back in words the model can act on, so it can fix the query and try again.
/// </summary>
internal static class SqlGuard
{
    /// <summary>The date parameter added to every read of the incidents: the earliest UK date the member may see.</summary>
    public const string HistoryParameter = "@HistoryFrom";

    public const string Schema = "reporting";
    public const string IncidentsView = "Incidents";
    public const string SubcategoriesView = "Subcategories";

    /// <summary>
    /// Aggregates: a column using one is a measure, blanked on hidden rows. STRING_AGG is left out on purpose: it
    /// would list the values inside a group (each place, say), which the "fewer than 3" rule can't hide.
    /// </summary>
    internal static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "COUNT_BIG", "SUM", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP",
        "PERCENTILE_CONT", "PERCENTILE_DISC", "APPROX_COUNT_DISTINCT",
    };

    /// <summary>Every other function the model may call: dates, text, maths and ranking. Anything else is refused.</summary>
    internal static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Dates (UK dates are already in the view; GETDATE is the server's clock, so the prompt prefers literals).
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATEFROMPARTS", "DATETRUNC", "EOMONTH",
        "YEAR", "MONTH", "DAY", "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME", "ISDATE",
        // Text.
        "UPPER", "LOWER", "LEN", "SUBSTRING", "LTRIM", "RTRIM", "TRIM", "CONCAT", "CONCAT_WS", "REPLACE",
        "CHARINDEX", "PATINDEX", "FORMAT", "STR", "REVERSE",
        // Maths.
        "ROUND", "ABS", "CEILING", "FLOOR", "POWER", "SQRT", "SQUARE", "SIGN", "GREATEST", "LEAST", "CHOOSE",
        "ISNULL",
        // Ranking and windows.
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "PERCENT_RANK",
        "CUME_DIST",
    };

    /// <summary>Checks <paramref name="sql"/> and, when it passes, adds the date limit.</summary>
    public static SqlCheck Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlCheck.Fail("The query is empty.");
        }

        if (sql.Length > 8000)
        {
            return SqlCheck.Fail("The query is too long. Keep it under 8,000 characters.");
        }

        var parsed = Parse(sql, out var errors);
        if (errors.Count > 0)
        {
            var error = errors[0];
            return SqlCheck.Fail($"That isn't valid T-SQL: {error.Message} (line {error.Line}, column {error.Column}).");
        }

        if (parsed is not TSqlScript script
            || script.Batches.Count != 1
            || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement select)
        {
            return SqlCheck.Fail("Send exactly one SELECT statement (you may start it with WITH), and nothing else.");
        }

        if (select.Into is not null)
        {
            return SqlCheck.Fail("SELECT ... INTO isn't allowed. Just SELECT.");
        }

        if (select.ComputeClauses.Count > 0 || select.OptimizerHints.Count > 0 || select.WithCtesAndXmlNamespaces?.XmlNamespaces is not null)
        {
            return SqlCheck.Fail("COMPUTE, OPTION (...) hints and XML namespaces aren't allowed.");
        }

        var top = Unwrap(select.QueryExpression);
        if (top is not QuerySpecification outer)
        {
            return SqlCheck.Fail("Use a single SELECT, not UNION, EXCEPT or INTERSECT. CASE inside one grouped query usually does the same job.");
        }

        if (outer.ForClause is not null)
        {
            return SqlCheck.Fail("FOR XML, FOR JSON and FOR BROWSE aren't allowed.");
        }

        var cteNames = select.WithCtesAndXmlNamespaces?.CommonTableExpressions
            .Select(c => c.ExpressionName.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var inspector = new Inspector(cteNames);
        select.Accept(inspector);
        if (inspector.Problem is { } problem)
        {
            return SqlCheck.Fail(problem);
        }

        if (inspector.IncidentReferences.Count > 1)
        {
            return SqlCheck.Fail(
                "Read reporting.Incidents only once per query (no self-joins or second subqueries). For a share of the total, use SUM(COUNT(*)) OVER ().");
        }

        if (inspector.CteUses.FirstOrDefault(c => c.Value > 1) is { Key: not null } reused)
        {
            return SqlCheck.Fail($"Use each WITH table once; {reused.Key} is used {reused.Value} times.");
        }

        var columns = DescribeColumns(outer);
        if (columns.Problem is { } columnProblem)
        {
            return SqlCheck.Fail(columnProblem);
        }

        var rewritten = inspector.IncidentReferences.Count == 1 ? AddDateLimit(sql, inspector.IncidentReferences[0]) : sql;

        // Belt and braces: the rewritten text must still parse.
        Parse(rewritten, out var rewriteErrors);
        if (rewriteErrors.Count > 0)
        {
            return SqlCheck.Fail("The query couldn't be limited to your plan's dates. Simplify the FROM clause and try again.");
        }

        return new SqlCheck(
            new CheckedQuery(rewritten, columns.IncidentCount, columns.Measures, inspector.IncidentReferences.Count == 1),
            null);
    }

    private static TSqlFragment Parse(string sql, out IList<ParseError> errors)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        return parser.Parse(reader, out errors);
    }

    private static QueryExpression Unwrap(QueryExpression expression)
    {
        while (expression is QueryParenthesisExpression parenthesis)
        {
            expression = parenthesis.QueryExpression;
        }

        return expression;
    }

    private static ScalarExpression Unwrap(ScalarExpression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesisExpression p:
                    expression = p.Expression;
                    break;
                case CastCall c:
                    expression = c.Parameter;
                    break;
                case ConvertCall c:
                    expression = c.Parameter;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static bool IsCount(ScalarExpression expression, out FunctionCall call)
    {
        call = null!;
        if (Unwrap(expression) is FunctionCall f
            && f.OverClause is null
            && f.CallTarget is null
            && f.FunctionName.Value.ToUpperInvariant() is "COUNT" or "COUNT_BIG")
        {
            call = f;
            return true;
        }

        return false;
    }

    private sealed record ColumnPlan(int IncidentCount, IReadOnlyList<int> Measures, string? Problem);

    /// <summary>Finds the outer SELECT's count and measure columns; it must have at least one COUNT.</summary>
    private static ColumnPlan DescribeColumns(QuerySpecification outer)
    {
        var counts = new List<(int Index, bool Distinct)>();
        var measures = new List<int>();
        for (var i = 0; i < outer.SelectElements.Count; i++)
        {
            switch (outer.SelectElements[i])
            {
                case SelectStarExpression:
                    return new ColumnPlan(-1, [], "Name the columns you need instead of SELECT *.");
                case SelectSetVariable:
                    return new ColumnPlan(-1, [], "Variables aren't allowed.");
                case SelectScalarExpression scalar:
                    if (IsCount(scalar.Expression, out var call))
                    {
                        counts.Add((i, call.UniqueRowFilter == UniqueRowFilter.Distinct));
                    }

                    if (UsesAggregate(scalar.Expression))
                    {
                        measures.Add(i);
                    }

                    break;
            }
        }

        if (counts.Count == 0)
        {
            return new ColumnPlan(-1, [],
                "Answers must be grouped counts: include COUNT(*) AS Incidents in the outer SELECT (with GROUP BY for anything you break it down by). Single incidents can't be listed.");
        }

        // A plain COUNT(*) or COUNT(column) counts incident rows; prefer it over COUNT(DISTINCT ...).
        var incidentCount = counts.FirstOrDefault(c => !c.Distinct, counts[0]).Index;
        return new ColumnPlan(incidentCount, measures, null);
    }

    private static bool UsesAggregate(ScalarExpression expression)
    {
        var finder = new AggregateFinder();
        expression.Accept(finder);
        return finder.Found;
    }

    /// <summary>
    /// Replaces the one read of <c>reporting.Incidents</c> with a derived table limited to the member's dates, keeping
    /// its alias (or "Incidents") so the rest of the query is unchanged.
    /// </summary>
    private static string AddDateLimit(string sql, NamedTableReference incidents)
    {
        var alias = incidents.Alias?.Value ?? IncidentsView;
        var limited =
            $"(SELECT * FROM {Schema}.{IncidentsView} WHERE IncidentDate >= {HistoryParameter}) AS [{alias.Replace("]", "]]", StringComparison.Ordinal)}]";
        return string.Concat(sql.AsSpan(0, incidents.StartOffset), limited, sql.AsSpan(incidents.StartOffset + incidents.FragmentLength));
    }

    private sealed class AggregateFinder : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is null && Aggregates.Contains(node.FunctionName.Value))
            {
                Found = true;
            }
        }
    }

    /// <summary>True when a join condition, at its top level of ANDs, says <c>x.Subcategory = y.Subcategory</c>.</summary>
    private static bool JoinsOnSubcategory(BooleanExpression? condition)
    {
        switch (condition)
        {
            case BooleanParenthesisExpression p:
                return JoinsOnSubcategory(p.Expression);
            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                return JoinsOnSubcategory(and.FirstExpression) || JoinsOnSubcategory(and.SecondExpression);
            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } equals:
                return IsSubcategoryColumn(equals.FirstExpression) && IsSubcategoryColumn(equals.SecondExpression);
            default:
                return false;
        }
    }

    private static bool IsSubcategoryColumn(ScalarExpression expression) =>
        Unwrap(expression) is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } parts }
        && string.Equals(parts[^1].Value, "Subcategory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Walks the whole statement, recording the views it reads and the first thing that isn't allowed.</summary>
    private sealed class Inspector(HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public List<NamedTableReference> IncidentReferences { get; } = [];

        public Dictionary<string, int> CteUses { get; } = new(StringComparer.OrdinalIgnoreCase);

        private void Refuse(string problem) => Problem ??= problem;

        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case UnqualifiedJoin:
                    Refuse("CROSS JOIN, CROSS APPLY and OUTER APPLY aren't allowed, nor commas between tables. Join with JOIN ... ON a.Subcategory = b.Subcategory.");
                    break;
                case BinaryQueryExpression:
                    Refuse("UNION, EXCEPT and INTERSECT aren't allowed anywhere. Use CASE inside one grouped query.");
                    break;
                case TableReference table when table is not (NamedTableReference or QueryDerivedTable or QualifiedJoin or JoinParenthesisTableReference):
                    Refuse("Only reporting.Incidents, reporting.Subcategories, WITH tables and (SELECT ...) subqueries can be used in FROM.");
                    break;
                case VariableReference:
                    Refuse("Variables aren't allowed. Write dates as literals, e.g. '2026-01-01'.");
                    break;
                case GlobalVariableExpression:
                    Refuse("@@ values aren't allowed.");
                    break;
                case TableHint:
                    Refuse("Table hints such as WITH (NOLOCK) aren't allowed.");
                    break;
                case FullTextPredicate or OdbcFunctionCall or NextValueForExpression:
                    Refuse("That function isn't allowed.");
                    break;
                case ColumnReferenceExpression { MultiPartIdentifier.Identifiers.Count: > 2 }:
                    Refuse("Give each table an alias and write alias.Column, not reporting.Incidents.Column.");
                    break;
            }
        }

        public override void Visit(FromClause node)
        {
            if (node.TableReferences.Count > 1)
            {
                Refuse("Commas between tables aren't allowed. Join with JOIN ... ON a.Subcategory = b.Subcategory.");
            }
        }

        public override void Visit(FunctionCall node)
        {
            var name = node.FunctionName.Value;
            if (node.CallTarget is not null)
            {
                Refuse($"Only built-in functions can be called; {name} isn't one.");
            }
            else if (!Aggregates.Contains(name) && !Functions.Contains(name))
            {
                Refuse($"The function {name.ToUpperInvariant()} isn't allowed. Stick to counts, sums, averages, dates, text and rounding.");
            }
        }

        public override void Visit(QualifiedJoin node)
        {
            if (!JoinsOnSubcategory(node.SearchCondition))
            {
                Refuse("Joins must match on Subcategory, e.g. JOIN reporting.Subcategories s ON s.Subcategory = i.Subcategory (other joins could count an incident twice).");
            }
        }

        public override void Visit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            var shown = string.Join('.', name.Identifiers.Select(i => i.Value));
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
            {
                Refuse($"Only reporting.Incidents and reporting.Subcategories can be read, not {shown}.");
                return;
            }

            var schema = name.SchemaIdentifier?.Value;
            var view = name.BaseIdentifier.Value;
            if (schema is null && cteNames.Contains(view))
            {
                CteUses[view] = CteUses.GetValueOrDefault(view) + 1;
                return;
            }

            if (!string.Equals(schema, Schema, StringComparison.OrdinalIgnoreCase))
            {
                Refuse($"Only reporting.Incidents and reporting.Subcategories can be read, not {shown}. Include the reporting. schema.");
                return;
            }

            if (string.Equals(view, IncidentsView, StringComparison.OrdinalIgnoreCase))
            {
                IncidentReferences.Add(node);
            }
            else if (!string.Equals(view, SubcategoriesView, StringComparison.OrdinalIgnoreCase))
            {
                Refuse($"Only reporting.Incidents and reporting.Subcategories can be read, not {shown}.");
            }
        }
    }
}
