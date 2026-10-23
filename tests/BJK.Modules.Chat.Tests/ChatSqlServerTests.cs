using System.Data;
using BJK.Modules.Chat.Agent;
using BJK.Modules.Chat.Infrastructure.Data;
using BJK.Modules.Chat.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BJK.Modules.Chat.Tests;

/// <summary>
/// Against a real SQL Server (CI runs one; set <c>BJK_TEST_SQLSERVER</c> to a connection string without a database
/// name to run locally). Each run makes a database with <c>reporting.Incidents</c> and <c>reporting.Subcategories</c>
/// tables shaped like the views and seeded with fixed rows, and a login that, like bjk_chat, can read only those two.
/// </summary>
public sealed class ChatSqlServerTests : IAsyncLifetime
{
    private static readonly DateOnly SeedToday = new(2026, 10, 23);

    private readonly string _database = "bjk_chat_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _login = "bjk_chat_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _password = "Chat-" + Guid.NewGuid().ToString("N") + "!1a";
    private string? _admin;
    private string? _chat;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Admin => _admin ?? throw SkipException();

    private string Chat => _chat ?? throw SkipException();

    public async ValueTask InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("BJK_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        var master = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(master, $"CREATE DATABASE [{_database}]; CREATE LOGIN [{_login}] WITH PASSWORD = N'{_password}', CHECK_POLICY = OFF;", Ct);

        _admin = new SqlConnectionStringBuilder(server) { InitialCatalog = _database }.ConnectionString;
        _chat = new SqlConnectionStringBuilder(server) { InitialCatalog = _database, UserID = _login, Password = _password }.ConnectionString;

        await ExecuteAsync(_admin, "CREATE SCHEMA reporting", Ct);
        await ExecuteAsync(_admin, $"""
            CREATE TABLE reporting.Subcategories (Position tinyint NOT NULL, Subcategory nvarchar(40) NOT NULL PRIMARY KEY,
                SubcategoryLabel nvarchar(80) NOT NULL, [Group] nvarchar(30) NOT NULL, GroupLabel nvarchar(60) NOT NULL);
            CREATE TABLE reporting.Incidents (IsMainType bit NOT NULL, Subcategory nvarchar(40) NOT NULL,
                SubcategoryLabel nvarchar(80) NOT NULL, [Group] nvarchar(30) NOT NULL, GroupLabel nvarchar(60) NOT NULL,
                IncidentDate date NOT NULL, IncidentYear smallint NOT NULL, IncidentMonth tinyint NOT NULL, Weekday tinyint NOT NULL,
                WeekdayName nvarchar(30) NOT NULL, HourUk tinyint NOT NULL, AreaLatitude decimal(5,2) NOT NULL,
                AreaLongitude decimal(5,2) NOT NULL, PlaceName nvarchar(200) NULL, VehicleType nvarchar(20) NULL,
                VehicleMake nvarchar(100) NULL, VehicleModel nvarchar(100) NULL, VehicleColour nvarchar(20) NULL, IsDark bit NOT NULL,
                ReportedToPolice bit NOT NULL, HasVideo bit NOT NULL, Reactions int NOT NULL, Comments int NOT NULL, Views int NOT NULL,
                LocalViews int NOT NULL);
            CREATE TABLE dbo.Secret (Id int NOT NULL);
            CREATE USER [{_login}] FOR LOGIN [{_login}];
            GRANT SELECT ON reporting.Incidents TO [{_login}];
            GRANT SELECT ON reporting.Subcategories TO [{_login}];
            """, Ct);
        await SeedAsync(_admin);
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null)
        {
            return;
        }

        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(_admin) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(
            master,
            $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; DROP LOGIN [{_login}];",
            CancellationToken.None);
    }

    public static TheoryData<string> WorkedExamples() => SqlGuardTests.WorkedExamples();

    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public async Task Every_worked_example_runs_through_the_chat_login(string sql)
    {
        var query = SqlGuard.Check(sql).Query!;

        var outcome = await Queries().RunAsync(query, SeedToday.AddDays(-365), Ct);

        Assert.Null(outcome.Problem);
        Assert.NotNull(outcome.Result);
    }

    [Fact]
    public async Task The_plan_date_limit_applies()
    {
        var query = SqlGuard.Check("SELECT COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1").Query!;

        var year = await CountAsync(query, SeedToday.AddDays(-365));
        var fiveYears = await CountAsync(query, SeedToday.AddDays(-1826));
        var direct = (int)(await ScalarAsync(Admin, $"SELECT COUNT(*) FROM reporting.Incidents WHERE IsMainType = 1 AND IncidentDate >= '{SeedToday.AddDays(-365):yyyy-MM-dd}'"))!;

        Assert.Equal(direct, year);
        Assert.True(fiveYears > year);
    }

    [Fact]
    public async Task Small_groups_come_back_hidden()
    {
        var query = SqlGuard.Check(
            "SELECT PlaceName AS Place, COUNT(*) AS Incidents, MAX(IncidentDate) AS Latest FROM reporting.Incidents WHERE PlaceName IS NOT NULL GROUP BY PlaceName ORDER BY PlaceName").Query!;

        var result = (await Queries().RunAsync(query, SeedToday.AddDays(-1826), Ct)).Result!;

        var lonely = result.Rows.Select((row, i) => (row, i)).Single(x => (string?)x.row[0] == "Lonely Lane, Huyton");
        Assert.True(result.Hidden[lonely.i]);
        Assert.Null(lonely.row[1]);
        Assert.Null(lonely.row[2]);
        var leeds = result.Rows.Single(row => (string?)row[0] == "Briggate, Leeds");
        Assert.True((int)leeds[1]! >= 3);
        Assert.IsType<DateOnly>(leeds[2]);
    }

    [Fact]
    public async Task The_login_can_read_nothing_else()
    {
        // Bypasses the parser on purpose: the database is the second line of defence.
        var outcome = await Queries().RunAsync(new CheckedQuery("SELECT COUNT(*) AS n FROM dbo.Secret", 0, [0], false), SeedToday, Ct);

        Assert.Null(outcome.Result);
        Assert.Contains("Only reporting.Incidents and reporting.Subcategories can be read", outcome.Problem);
    }

    [Fact]
    public async Task Stops_at_a_thousand_rows()
    {
        var outcome = await Queries().RunAsync(
            new CheckedQuery("SELECT IncidentDate, HourUk, Subcategory, 5 AS n FROM reporting.Incidents", 3, [3], true), SeedToday, Ct);

        Assert.Equal(ChatOptions.MaxRows, outcome.Result!.Rows.Count);
        Assert.True(outcome.Result.Truncated);
    }

    [Fact]
    public async Task Stops_a_query_after_five_seconds()
    {
        var outcome = await Queries().RunAsync(new CheckedQuery("WAITFOR DELAY '00:00:08'; SELECT 1 AS n", 0, [0], false), SeedToday, Ct);

        Assert.Contains("longer than 5 seconds", outcome.Problem);
    }

    [Fact]
    public async Task Daily_counts_never_go_over_the_limit_even_all_at_once()
    {
        await using (var db = Context())
        {
            await db.Database.MigrateAsync(Ct);
        }

        var userId = Guid.NewGuid();
        var day = new DateOnly(2026, 10, 23);
        var takes = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            await using var db = Context();
            return await new ChatUsage(db).TryTakeAsync(userId, day, 5, Ct);
        }));

        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, takes.Where(t => t is not null).Order().ToArray());

        await using (var db = Context())
        {
            var usage = new ChatUsage(db);
            Assert.Equal(new DailyCounts(5, 0), await usage.GetAsync(userId, day, Ct));

            await usage.GiveBackAsync(userId, day, Ct);
            Assert.Equal(4, (await usage.GetAsync(userId, day, Ct)).Questions);

            // Strikes count separately and never touch the allowance.
            Assert.Equal(1, await usage.AddStrikeAsync(userId, day, Ct));
            Assert.Equal(2, await usage.AddStrikeAsync(userId, day, Ct));
            Assert.Equal(new DailyCounts(4, 2), await usage.GetAsync(userId, day, Ct));
            Assert.Equal(1, await usage.AddStrikeAsync(Guid.NewGuid(), day, Ct)); // a member's first event of the day

            // The next day starts again and clears the old row.
            Assert.Equal(1, await usage.TryTakeAsync(userId, day.AddDays(1), 5, Ct));
            Assert.Equal(1, await db.DailyUsage.CountAsync(u => u.UserId == userId, Ct));

            await usage.DeleteForUserAsync(userId, Ct);
            Assert.Equal(0, await db.DailyUsage.CountAsync(u => u.UserId == userId, Ct));
        }
    }

    private SqlIncidentQueries Queries() => new(new ChatDatabase(Chat));

    private ChatDbContext Context() => new(new DbContextOptionsBuilder<ChatDbContext>()
        .UseSqlServer(Admin, sql => sql.MigrationsHistoryTable(ChatModule.MigrationsHistoryTable, ChatModule.Schema))
        .Options);

    private async Task<int> CountAsync(CheckedQuery query, DateOnly from)
    {
        var result = (await Queries().RunAsync(query, from, Ct)).Result!;
        return (int)result.Rows[0][0]!;
    }

    private static readonly (string Subcategory, string Label, string Group, string GroupLabel)[] Kinds =
    [
        ("PavementParking", "Parked on the pavement", "Parking", "Parking"),
        ("SchoolZigZags", "On school zig-zags", "Parking", "Parking"),
        ("SpeedingInTown", "Speeding in a 20 or 30", "Speeding", "Speeding"),
        ("Tailgating", "Tailgating", "FollowingTooClose", "Following too close"),
        ("RanRedLight", "Ran a red light", "RedLightsAndCrossings", "Red lights and crossings"),
        ("ClosePassCyclist", "Close pass of a cyclist", "VulnerableRoadUsers", "Cyclists, pedestrians and riders"),
        ("ClosePassHorse", "Close pass of a horse rider", "VulnerableRoadUsers", "Cyclists, pedestrians and riders"),
        ("ShoutingOrGestures", "Shouting or gestures", "RoadRage", "Road rage"),
        ("FollowingOrChasing", "Following or chasing", "RoadRage", "Road rage"),
        ("NoLightsAtNight", "No lights at night", "VehiclesAndDrivers", "Vehicles and drivers"),
        ("StoppingOnMotorway", "Stopping or reversing on a motorway", "Motorways", "Motorways"),
        ("WrongWayDriver", "Wrong-way driver", "Motorways", "Motorways"),
    ];

    private static readonly (string? Place, decimal Lat, decimal Lng)[] Places =
    [
        ("High Street, Prescot", 53.43m, -2.80m),
        ("Warrington Road, Prescot", 53.42m, -2.79m),
        ("Briggate, Leeds", 53.80m, -1.54m),
        ("The Headrow, Leeds", 53.80m, -1.55m),
        ("Sankey Street, Warrington", 53.39m, -2.60m),
        ("Bridge Street, Warrington", 53.39m, -2.59m),
        (null, 53.40m, -2.98m),
    ];

    private static async Task SeedAsync(string connectionString)
    {
        var subcategories = new DataTable();
        foreach (var column in new[] { "Position", "Subcategory", "SubcategoryLabel", "Group", "GroupLabel" })
        {
            subcategories.Columns.Add(column, column == "Position" ? typeof(byte) : typeof(string));
        }

        for (var i = 0; i < Kinds.Length; i++)
        {
            subcategories.Rows.Add((byte)i, Kinds[i].Subcategory, Kinds[i].Label, Kinds[i].Group, Kinds[i].GroupLabel);
        }

        var incidents = new DataTable();
        (string Name, Type Type)[] columns =
        [
            ("IsMainType", typeof(bool)), ("Subcategory", typeof(string)), ("SubcategoryLabel", typeof(string)), ("Group", typeof(string)),
            ("GroupLabel", typeof(string)), ("IncidentDate", typeof(DateTime)), ("IncidentYear", typeof(short)), ("IncidentMonth", typeof(byte)),
            ("Weekday", typeof(byte)), ("WeekdayName", typeof(string)), ("HourUk", typeof(byte)), ("AreaLatitude", typeof(decimal)),
            ("AreaLongitude", typeof(decimal)), ("PlaceName", typeof(string)), ("VehicleType", typeof(string)), ("VehicleMake", typeof(string)),
            ("VehicleModel", typeof(string)), ("VehicleColour", typeof(string)), ("IsDark", typeof(bool)), ("ReportedToPolice", typeof(bool)),
            ("HasVideo", typeof(bool)), ("Reactions", typeof(int)), ("Comments", typeof(int)), ("Views", typeof(int)), ("LocalViews", typeof(int)),
        ];
        foreach (var (name, type) in columns)
        {
            incidents.Columns.Add(name, type);
        }

        string?[] vehicles = ["Car", "Car", "Car", "Van", "Lorry", "Bus", "Taxi", "Motorbike", null];
        string?[] makes = ["Ford", "BMW", "Audi", "Vauxhall", "Toyota", null];
        string?[] colours = ["Black", "White", "Silver", "Blue", "Red", null];
        var random = new Random(42);
        void Add(bool main, int kind, DateOnly date, int hour, int place)
        {
            var k = Kinds[kind];
            var p = Places[place];
            incidents.Rows.Add(
                main, k.Subcategory, k.Label, k.Group, k.GroupLabel, date.ToDateTime(TimeOnly.MinValue), (short)date.Year, (byte)date.Month,
                (byte)(((int)date.DayOfWeek + 6) % 7 + 1), date.DayOfWeek.ToString(), (byte)hour, p.Lat, p.Lng, (object?)p.Place ?? DBNull.Value,
                (object?)vehicles[random.Next(vehicles.Length)] ?? DBNull.Value, (object?)makes[random.Next(makes.Length)] ?? DBNull.Value,
                DBNull.Value, (object?)colours[random.Next(colours.Length)] ?? DBNull.Value, hour is < 7 or > 19, random.Next(4) == 0,
                random.Next(2) == 0, random.Next(50), random.Next(10), random.Next(500), random.Next(100));
        }

        for (var i = 0; i < 1400; i++)
        {
            var kind = random.Next(Kinds.Length);
            var date = SeedToday.AddDays(-random.Next(730));
            var hour = random.Next(24);
            var place = random.Next(Places.Length);
            Add(true, kind, date, hour, place);
            if (random.Next(6) == 0)
            {
                // A second subcategory in the same group, as an extra row.
                Add(false, kind % 2 == 0 && kind + 1 < Kinds.Length ? kind + 1 : kind, date, hour, place);
            }
        }

        // One lonely incident, so a small group shows up.
        incidents.Rows.Add(
            true, "Tailgating", "Tailgating", "FollowingTooClose", "Following too close", SeedToday.AddDays(-3).ToDateTime(TimeOnly.MinValue),
            (short)2026, (byte)10, (byte)2, "Tuesday", (byte)8, 53.41m, -2.84m, "Lonely Lane, Huyton", "Car", "Ford", DBNull.Value, "Red",
            false, false, true, 1, 0, 12, 3);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var (table, data) in new[] { ("reporting.Subcategories", subcategories), ("reporting.Incidents", incidents) })
        {
            using var copy = new SqlBulkCopy(connection) { DestinationTableName = table };
            foreach (DataColumn column in data.Columns)
            {
                copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await copy.WriteToServerAsync(data, Ct);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private static Exception SkipException()
    {
        Assert.Skip("BJK_TEST_SQLSERVER isn't set.");
        return new InvalidOperationException();
    }
}
