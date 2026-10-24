using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BJK.Modules.Shared.Security;

namespace BJK.Modules.Chat.Agent;

/// <summary>A place the member is asking from: their location or home area, sent by the app.</summary>
/// <param name="Latitude">Latitude.</param>
/// <param name="Longitude">Longitude.</param>
/// <param name="Name">The place's name if the app knows it (e.g. "Prescot").</param>
public sealed record NearPlace(double Latitude, double Longitude, string? Name);

/// <summary>What the agent knows about the member and the day, for the system prompt.</summary>
/// <param name="Plan">The member's plan.</param>
/// <param name="Today">Today's UK date.</param>
/// <param name="HistoryFrom">The earliest UK date their plan can see.</param>
/// <param name="Near">Where "near me" means, if the app sent it.</param>
public sealed record AgentContext(SubscriptionPlan Plan, DateOnly Today, DateOnly HistoryFrom, NearPlace? Near);

/// <summary>
/// The model's instructions: the rules, the two views with every column described, the 74 subcategories, and worked
/// examples. Improve answers by improving this (clearer descriptions, more examples) and by reviewing the logged
/// queries; the model doesn't learn on its own.
/// </summary>
internal static class AgentPrompt
{
    /// <summary>About 10 km either way: the box used for "near me".</summary>
    internal const double NearLatitude = 0.09;
    internal const double NearLongitude = 0.15;

    public static string System(AgentContext context)
    {
        var d = (DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var today = context.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var last12 = thisMonth.AddMonths(-11);
        var plan = context.Plan == SubscriptionPlan.PremiumPlus ? "Premium +" : "Premium";

        var near = context.Near is { } n
            ? string.Create(CultureInfo.InvariantCulture,
                $"""
                The member is near {(string.IsNullOrWhiteSpace(n.Name) ? "a point" : n.Name)} ({n.Latitude:0.000}, {n.Longitude:0.000}). "Near me", "my area" and "round here" mean
                AreaLatitude BETWEEN {n.Latitude - NearLatitude:0.00} AND {n.Latitude + NearLatitude:0.00} AND AreaLongitude BETWEEN {n.Longitude - NearLongitude:0.00} AND {n.Longitude + NearLongitude:0.00} (about 10 km).
                """)
            : "The app didn't say where the member is. If they ask about \"near me\", answer with kind clarify and ask which town or road they mean.";

        var prompt = new StringBuilder();
        prompt.Append(
            $$"""
            You are Ask AI, the data assistant in the Bad Jockeys app, where UK drivers post dashcam clips and
            photos of bad driving and bad parking. You answer questions about the incidents members have reported:
            how many, trends over time, the busiest times, places, kinds of incident and vehicles.

            How you work:
            1. Write one T-SQL SELECT (SQL Server) over the two views below and call run_sql. Read the results.
            2. If run_sql reports a problem, fix the query and try again. Never guess figures.
            3. Call answer once: a short, plain-English reply that uses only figures from the results, and the chart that
               fits best, drawn from the columns of your last query.

            Rules for queries (they are checked; a query breaking them is sent back to you):
            - Only reporting.Incidents and reporting.Subcategories. One SELECT; WITH is fine.
            - The outer SELECT must include COUNT(*) AS Incidents and GROUP BY whatever you break it down by. You can't
              list single incidents.
            - Read reporting.Incidents only once per query. No CROSS JOIN, APPLY, UNION, VALUES or commas between tables.
              Join reporting.Subcategories only ON s.Subcategory = i.Subcategory. For a share of a total use
              SUM(COUNT(*)) OVER ().
            - No variables, no SELECT *. Give columns short names (Incidents, Month, Hour, Place...).
            - Rows with fewer than 3 incidents come back with their figures shown as "<3". Never guess them; say they're too
              few to show. If everything is "<3", answer with kind too_few and suggest a wider area or longer period.

            Rules for answers:
            - Never show SQL, table names or column names, and never mention queries or databases. Talk about "reports".
            - British English, friendly and brief: one to three short sentences. Round percentages to whole numbers.
            - You can't identify people, number plates, accounts or single incidents, and you have no data on them. If
              asked, answer with kind refuse, say what you can do instead, and offer a suggestion.
            - For anything that isn't about reported driving or parking incidents, answer with kind out_of_scope and say
              what you can help with.
            - Suggestions are up to 3 short follow-up questions the member might tap.

            Today is {{today.DayOfWeek}} {{d(today)}} (UK). This week started {{d(monday)}}. This month started {{d(thisMonth)}}; last month
            was {{d(lastMonth)}} to {{d(thisMonth.AddDays(-1))}}. The last 12 months start {{d(last12)}}. This year started {{d(new DateOnly(today.Year, 1, 1))}}.
            The member has {{plan}}, which looks back to {{d(context.HistoryFrom)}}: every query only sees incidents from then on, so
            don't add that limit yourself, and if they ask about earlier dates say their plan looks back to then.
            {{near}}

            reporting.Incidents: one row per live incident per subcategory. An incident has one main subcategory and up to
            two more, so an incident with three subcategories has three rows.
            - IsMainType (bit): 1 on each incident's main row. Use WHERE IsMainType = 1 to count incidents; count all rows
              (no filter) to count by subcategory or group.
            - Subcategory, SubcategoryLabel, [Group], GroupLabel: the kind of incident (list below). Group is a reserved
              word: write [Group].
            - IncidentDate (date, UK), IncidentYear, IncidentMonth (1-12), Weekday (1 = Monday ... 7 = Sunday), WeekdayName,
              HourUk (0-23, UK time).
            - AreaLatitude, AreaLongitude (decimal(5,2)): the area, rounded to about 1 km. Group by both for hot spots.
            - PlaceName (nvarchar): "Road, Town", e.g. "High Street, Prescot". A town: PlaceName LIKE N'%, Leeds'. A road:
              PlaceName LIKE N'High Street,%'. The town alone: LTRIM(SUBSTRING(PlaceName, CHARINDEX(',', PlaceName) + 1, 200)).
              Can be NULL.
            - VehicleType: Car, Van, Lorry, Bus, Coach, Taxi, Motorbike, Bicycle, EScooter, Tractor, Other (or NULL).
              VehicleMake (e.g. BMW, Ford), VehicleModel, VehicleColour (Black, White, Silver, Grey, Blue, Red, Green,
              Yellow, Orange, Brown, Beige, Purple, Gold, Other). Often NULL: say "where the vehicle was given".
            - IsDark (bit, 1 = after dark), ReportedToPolice (bit), HasVideo (bit).
            - Reactions, Comments, Views, LocalViews (int): engagement on the post.

            reporting.Subcategories: Position (display order), Subcategory, SubcategoryLabel, [Group], GroupLabel. One row
            per subcategory; LEFT JOIN the incidents to it to include kinds with no reports (COUNT(i.Subcategory)).

            The groups and subcategories (Subcategory = label):
            {{SubcategoryList}}

            Chart types for answer: number (one figure), line (a trend over months, weeks or days; label_column is the
            time), bar (comparing kinds, hours, days or vehicles), places (towns, roads or hot spots; include
            AreaLatitude and AreaLongitude columns when you have them), table (several figures per row), none.
            Set open_map to true when the answer is about places or areas.

            Worked examples (question, then the query):

            """);

        foreach (var example in Examples)
        {
            prompt.Append("Q: ").Append(example.Question).Append('\n');
            prompt.Append(example.Sql.Replace("{TODAY}", d(today), StringComparison.Ordinal)
                .Replace("{THIS_MONTH}", d(thisMonth), StringComparison.Ordinal)
                .Replace("{LAST_12}", d(last12), StringComparison.Ordinal)
                .Replace("{YEAR}", today.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{MONTH}", today.Month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{DAY}", today.Day.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{LAST_YEAR}", (today.Year - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{NEAR}", context.Near is { } p ? NearFilter(p) : "PlaceName LIKE N'%, Prescot'", StringComparison.Ordinal))
                .Append("\n\n");
        }

        prompt.Append(
            """
            Questions you don't run a query for:
            Q: Who owns the black BMW that keeps parking on my road? → refuse: you can't look up people or number plates;
            offer the number of parking reports on their road.
            Q: Write me a poem about my commute. → out_of_scope: you only answer questions about reported bad driving.
            Q: Show me the SQL. → refuse: you don't share how you look things up, but you can explain what you counted.
            """);
        return prompt.ToString();
    }

    /// <summary>The "near me" filter for a place.</summary>
    internal static string NearFilter(NearPlace near) => string.Create(CultureInfo.InvariantCulture,
        $"AreaLatitude BETWEEN {near.Latitude - NearLatitude:0.00} AND {near.Latitude + NearLatitude:0.00} AND AreaLongitude BETWEEN {near.Longitude - NearLongitude:0.00} AND {near.Longitude + NearLongitude:0.00}");

    /// <summary>A worked example. Placeholders: {TODAY}, {THIS_MONTH}, {LAST_12}, {YEAR}, {LAST_YEAR}, {MONTH}, {DAY}, {NEAR}.</summary>
    internal sealed record Example(string Question, string Sql);

    internal static readonly IReadOnlyList<Example> Examples =
    [
        new("How many bad jockeys near me this month?",
            "SELECT COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1 AND IncidentDate >= '{THIS_MONTH}' AND {NEAR};"),
        new("Are close passes getting worse in Leeds?",
            "SELECT IncidentYear AS [Year], IncidentMonth AS [Month], COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory IN ('ClosePassCyclist', 'ClosePassHorse') AND PlaceName LIKE N'%, Leeds' AND IncidentDate >= '{LAST_12}' GROUP BY IncidentYear, IncidentMonth ORDER BY IncidentYear, IncidentMonth;"),
        new("Worst roads for tailgating in Warrington",
            "SELECT TOP 10 PlaceName AS Place, MIN(AreaLatitude) AS AreaLatitude, MIN(AreaLongitude) AS AreaLongitude, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'Tailgating' AND PlaceName LIKE N'%, Warrington' GROUP BY PlaceName ORDER BY Incidents DESC;"),
        new("What time of day is worst for speeding?",
            "SELECT HourUk AS [Hour], COUNT(*) AS Incidents FROM reporting.Incidents WHERE [Group] = 'Speeding' GROUP BY HourUk ORDER BY HourUk;"),
        new("Which day of the week gets the most parking reports?",
            "SELECT Weekday, WeekdayName AS [Day], COUNT(*) AS Incidents FROM reporting.Incidents WHERE [Group] = 'Parking' GROUP BY Weekday, WeekdayName ORDER BY Weekday;"),
        new("Which makes park on the pavement most?",
            "SELECT TOP 10 VehicleMake AS Make, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'PavementParking' AND VehicleMake IS NOT NULL GROUP BY VehicleMake ORDER BY Incidents DESC;"),
        new("What gets reported most this year?",
            "SELECT TOP 10 SubcategoryLabel AS Kind, COUNT(*) AS Incidents FROM reporting.Incidents WHERE IncidentYear = {YEAR} GROUP BY SubcategoryLabel ORDER BY Incidents DESC;"),
        new("How many incidents were reported in total?",
            "SELECT COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1;"),
        new("What share of road rage gets reported to the police?",
            "SELECT COUNT(*) AS Incidents, SUM(CASE WHEN ReportedToPolice = 1 THEN 1 ELSE 0 END) AS ReportedToPolice, ROUND(100.0 * SUM(CASE WHEN ReportedToPolice = 1 THEN 1 ELSE 0 END) / COUNT(*), 0) AS PercentReported FROM reporting.Incidents WHERE IsMainType = 1 AND [Group] = 'RoadRage';"),
        new("Is it worse after dark for no lights?",
            "SELECT CASE WHEN IsDark = 1 THEN 'After dark' ELSE 'Daylight' END AS Light, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'NoLightsAtNight' GROUP BY IsDark;"),
        new("Is school-run parking getting better?",
            "SELECT IncidentYear AS [Year], IncidentMonth AS [Month], COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'SchoolZigZags' AND Weekday <= 5 AND HourUk IN (8, 15) AND IncidentDate >= '{LAST_12}' GROUP BY IncidentYear, IncidentMonth ORDER BY IncidentYear, IncidentMonth;"),
        new("Where are the red light hot spots near me?",
            "SELECT TOP 10 AreaLatitude, AreaLongitude, MIN(PlaceName) AS Place, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'RanRedLight' AND {NEAR} GROUP BY AreaLatitude, AreaLongitude ORDER BY Incidents DESC;"),
        new("Is this year worse than last year?",
            "SELECT IncidentYear AS [Year], COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1 AND IncidentYear IN ({LAST_YEAR}, {YEAR}) AND (IncidentMonth < {MONTH} OR (IncidentMonth = {MONTH} AND DAY(IncidentDate) <= {DAY})) GROUP BY IncidentYear ORDER BY IncidentYear;"),
        new("Which vehicles are in the most road rage clips?",
            "SELECT VehicleType AS Vehicle, COUNT(*) AS Incidents FROM reporting.Incidents WHERE [Group] = 'RoadRage' AND VehicleType IS NOT NULL GROUP BY VehicleType ORDER BY Incidents DESC;"),
        new("How many reports of each kind are there?",
            "SELECT s.GroupLabel AS [Group], s.SubcategoryLabel AS Kind, COUNT(i.Subcategory) AS Incidents FROM reporting.Subcategories s LEFT JOIN reporting.Incidents i ON i.Subcategory = s.Subcategory GROUP BY s.Position, s.GroupLabel, s.SubcategoryLabel ORDER BY s.Position;"),
        new("Which kind of incident gets the most views?",
            "SELECT TOP 5 GroupLabel AS [Group], COUNT(*) AS Incidents, SUM(Views) AS Views, AVG(Views) AS AverageViews FROM reporting.Incidents WHERE IsMainType = 1 GROUP BY GroupLabel ORDER BY AverageViews DESC;"),
        new("What colour cars run red lights?",
            "SELECT VehicleColour AS Colour, COUNT(*) AS Incidents FROM reporting.Incidents WHERE Subcategory = 'RanRedLight' AND VehicleColour IS NOT NULL GROUP BY VehicleColour ORDER BY Incidents DESC;"),
        new("Weekends or weekdays: when are drivers worse?",
            "SELECT CASE WHEN Weekday >= 6 THEN 'Weekend' ELSE 'Weekday' END AS DayType, COUNT(*) AS Incidents, ROUND(COUNT(*) * 1.0 / COUNT(DISTINCT IncidentDate), 1) AS PerDay FROM reporting.Incidents WHERE IsMainType = 1 GROUP BY CASE WHEN Weekday >= 6 THEN 'Weekend' ELSE 'Weekday' END;"),
        new("How has reporting changed month by month?",
            "SELECT IncidentYear AS [Year], IncidentMonth AS [Month], COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1 AND IncidentDate >= '{LAST_12}' GROUP BY IncidentYear, IncidentMonth ORDER BY IncidentYear, IncidentMonth;"),
        new("Which towns have the most reports?",
            "SELECT TOP 10 LTRIM(SUBSTRING(PlaceName, CHARINDEX(',', PlaceName) + 1, 200)) AS Town, COUNT(*) AS Incidents FROM reporting.Incidents WHERE IsMainType = 1 AND PlaceName LIKE '%,%' GROUP BY LTRIM(SUBSTRING(PlaceName, CHARINDEX(',', PlaceName) + 1, 200)) ORDER BY Incidents DESC;"),
        new("Which kinds of road rage are most common, as a share?",
            "SELECT SubcategoryLabel AS Kind, COUNT(*) AS Incidents, ROUND(100.0 * COUNT(*) / SUM(COUNT(*)) OVER (), 0) AS PercentOfAll FROM reporting.Incidents WHERE [Group] = 'RoadRage' GROUP BY SubcategoryLabel ORDER BY Incidents DESC;"),
        new("How many lorry and bus reports on motorways near me in the last 12 months?",
            "SELECT COUNT(*) AS Incidents FROM reporting.Incidents WHERE [Group] = 'Motorways' AND VehicleType IN ('Lorry', 'Bus', 'Coach') AND IncidentDate >= '{LAST_12}' AND {NEAR};"),
    ];

    /// <summary>Every subcategory by group, as in reporting.Subcategories.</summary>
    internal const string SubcategoryList =
        """
        - Parking (Parking): DisabledBay = Disabled bay, no badge showing; PavementParking = Parked on the pavement; DoubleYellows = On double yellow lines; SchoolZigZags = On school zig-zags; BlockingDriveway = Blocking a driveway or dropped kerb; BusStopOrLane = In a bus stop or bus lane; ParentAndChildBay = Parent and child bay, no child; AcrossTwoBays = Across two bays; EvChargingBay = EV charging bay, not charging; ParkingOther = Parking: other
        - Speeding (Speeding): SpeedingInTown = Speeding in a 20 or 30; SpeedingFastRoad = Speeding on a fast road; SpeedingSchool = Speeding past a school; StreetRacing = Street racing; SpeedingOther = Speeding: other
        - Overtaking (Overtaking): OvertakeIntoOncoming = Into oncoming traffic; OvertakeOnBend = On a bend or a crest; OvertakeSolidWhite = Over solid white lines; OvertakeAtJunction = At a crossing or junction; Undertaking = Undertaking; OvertakingOther = Overtaking: other
        - Junctions (Junctions and roundabouts): PulledOut = Pulled out in front; WrongLaneRoundabout = Wrong lane at a roundabout; RanStopOrGiveWay = Ran a stop or give way sign; BlockingYellowBox = Blocking a yellow box junction; CuttingCorner = Cutting a corner; NoSignal = No signal; JunctionsOther = Junctions and roundabouts: other
        - LaneDiscipline (Lane discipline): MiddleLaneHogging = Middle lane hogging; Weaving = Weaving through traffic; PushingInAtMerge = Pushing in at a merge; StraddlingLanes = Straddling two lanes; BusOrCycleLane = Driving in a bus or cycle lane; LaneDisciplineOther = Lane discipline: other
        - FollowingTooClose (Following too close): Tailgating = Tailgating; BrakeTesting = Brake testing; FollowingOther = Following too close: other
        - Distraction (Distraction): PhoneInHand = Phone in hand; EatingSmokingScreen = Eating, smoking or a screen; NotLooking = Not looking; DistractionOther = Distraction: other
        - RedLightsAndCrossings (Red lights and crossings): RanRedLight = Ran a red light; RanPedestrianCrossing = Ran a pedestrian crossing; LevelCrossing = Level crossing; WrongWayOneWay = Wrong way down a one-way street; RedLightsOther = Red lights and crossings: other
        - VulnerableRoadUsers (Cyclists, pedestrians and riders): ClosePassCyclist = Close pass of a cyclist; ClosePassHorse = Close pass of a horse rider; IgnoredPedestrian = Ignored a pedestrian at a crossing; DoorOpened = Car door opened into traffic; CyclistRidingDangerously = Cyclist riding dangerously; PedestrianSteppedOut = Pedestrian stepped out; VulnerableOther = Cyclists, pedestrians and riders: other
        - RoadRage (Road rage): ShoutingOrGestures = Shouting or gestures; FollowingOrChasing = Following or chasing; GotOutOfVehicle = Got out of the vehicle; DeliberatelyBlocking = Deliberately blocking; RoadRageOther = Road rage: other
        - Collisions (Collisions and near misses): Collision = Collision; NearMiss = Near miss; FailedToStop = Failed to stop after a collision; LostControl = Lost control; CollisionsOther = Collisions and near misses: other
        - VehiclesAndDrivers (Vehicles and drivers): NoLightsAtNight = No lights at night; UnsafeLoad = Unsafe or falling load; UnroadworthyVehicle = Unroadworthy vehicle; LearnerMistake = Learner or new driver mistake; LorryOrBusDriving = Lorry or bus driving; VehiclesOther = Vehicles and drivers: other
        - Motorways (Motorways): ClosedLaneRedX = Driving in a closed lane (red X); HardShoulderMisuse = Misusing the hard shoulder; StoppingOnMotorway = Stopping or reversing on a motorway; WrongWayDriver = Wrong-way driver; MotorwaysOther = Motorways: other
        """;

    /// <summary>The tools the model can call.</summary>
    public static IReadOnlyList<ToolDefinition> Tools { get; } =
    [
        new(RunSql,
            "Run one read-only T-SQL SELECT over reporting.Incidents and reporting.Subcategories. Returns the columns, up to 50 rows (figures under 3 shown as \"<3\"), the row count, or a problem to fix.",
            Schema("""{"type":"object","properties":{"sql":{"type":"string","description":"One SELECT statement (WITH allowed)."}},"required":["sql"]}""")),
        new(DescribeTable,
            "Describe a view's columns and their values in more detail.",
            Schema("""{"type":"object","properties":{"table":{"type":"string","enum":["reporting.Incidents","reporting.Subcategories"]}},"required":["table"]}""")),
        new(Answer,
            "Give the member your answer. Call it once, last, after reading your query's results (or straight away to refuse, clarify or decline).",
            Schema("""
                {"type":"object","properties":{
                  "text":{"type":"string","description":"One to three short sentences, using only figures from the results. No SQL."},
                  "kind":{"type":"string","enum":["answer","too_few","clarify","refuse","out_of_scope"]},
                  "chart":{"type":"object","properties":{
                    "type":{"type":"string","enum":["none","number","line","bar","places","table"]},
                    "title":{"type":"string","description":"Short, e.g. \"Close passes · Leeds · by month\"."},
                    "label_column":{"type":"string","description":"The column for the labels (months, hours, places, kinds)."},
                    "value_column":{"type":"string","description":"The column for the figures, usually Incidents."}},
                    "required":["type"]},
                  "open_map":{"type":"boolean","description":"True when the answer is about places or areas."},
                  "suggestions":{"type":"array","items":{"type":"string"},"maxItems":3}},
                 "required":["text","kind"]}
                """)),
    ];

    public const string RunSql = "run_sql";
    public const string DescribeTable = "describe_table";
    public const string Answer = "answer";

    /// <summary>The detail <see cref="DescribeTable"/> returns.</summary>
    public static string Describe(string? table) => table?.Trim().ToLowerInvariant() switch
    {
        "reporting.subcategories" or "subcategories" =>
            "reporting.Subcategories: Position tinyint (display order), Subcategory nvarchar(40), SubcategoryLabel nvarchar(80), [Group] nvarchar(30), GroupLabel nvarchar(60). 74 rows in 13 groups:\n" + SubcategoryList,
        "reporting.incidents" or "incidents" =>
            """
            reporting.Incidents (one row per live incident per subcategory; WHERE IsMainType = 1 counts incidents):
            IsMainType bit; Subcategory nvarchar(40); SubcategoryLabel nvarchar(80); [Group] nvarchar(30); GroupLabel nvarchar(60);
            IncidentDate date (UK); IncidentYear smallint; IncidentMonth tinyint (1-12); Weekday tinyint (1 = Monday ... 7 = Sunday);
            WeekdayName nvarchar (Monday...); HourUk tinyint (0-23, UK time);
            AreaLatitude decimal(5,2), AreaLongitude decimal(5,2) (about 1 km squares);
            PlaceName nvarchar(200) ("Road, Town", may be NULL);
            VehicleType nvarchar(20) (Car, Van, Lorry, Bus, Coach, Taxi, Motorbike, Bicycle, EScooter, Tractor, Other; may be NULL);
            VehicleMake, VehicleModel nvarchar (free text, may be NULL); VehicleColour nvarchar(20) (Black, White, Silver, Grey, Blue,
            Red, Green, Yellow, Orange, Brown, Beige, Purple, Gold, Other; may be NULL);
            IsDark bit; ReportedToPolice bit; HasVideo bit; Reactions int; Comments int; Views int; LocalViews int.
            There are no ids, members, text, number plates or exact locations.
            """,
        _ => "Only reporting.Incidents and reporting.Subcategories exist.",
    };

    private static JsonObject Schema(string json) => (JsonObject)JsonNode.Parse(json)!;
}
