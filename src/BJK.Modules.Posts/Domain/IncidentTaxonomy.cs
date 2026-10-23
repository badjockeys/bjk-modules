namespace BJK.Modules.Posts.Domain;

/// <summary>One answer to a post's vote, in the words its question uses.</summary>
public sealed record VoteAnswer(VoteChoice Choice, string Label);

/// <summary>A subcategory's vote: the question, and two to four answers in order.</summary>
public sealed record VoteQuestion(string Question, IReadOnlyList<VoteAnswer> Answers)
{
    public bool Offers(VoteChoice choice) => Answers.Any(a => a.Choice == choice);

    public string LabelOf(VoteChoice choice) => Answers.FirstOrDefault(a => a.Choice == choice)?.Label ?? choice.ToString();
}

/// <summary>A subcategory, as listed: its group, its name, and its vote.</summary>
public sealed record IncidentType(IncidentCategory Category, IncidentGroup Group, string Label, VoteQuestion Vote);

/// <summary>
/// The categories, subcategories and votes the owner signed off (doc "Bad Jockeys: categories, subcategories and
/// votes", 23 Oct 2026): 13 groups, 61 subcategories plus an "Other" in each group, and each subcategory's own vote.
/// A post has up to <see cref="MaxPerPost"/> subcategories; the first is the main one and its vote is the post's.
/// </summary>
public static class IncidentTaxonomy
{
    /// <summary>How many subcategories one post can have.</summary>
    public const int MaxPerPost = 3;

    private static readonly IReadOnlyDictionary<IncidentGroup, string> GroupLabels = new Dictionary<IncidentGroup, string>
    {
        [IncidentGroup.Parking] = "Parking",
        [IncidentGroup.Speeding] = "Speeding",
        [IncidentGroup.Overtaking] = "Overtaking",
        [IncidentGroup.Junctions] = "Junctions and roundabouts",
        [IncidentGroup.LaneDiscipline] = "Lane discipline",
        [IncidentGroup.FollowingTooClose] = "Following too close",
        [IncidentGroup.Distraction] = "Distraction",
        [IncidentGroup.RedLightsAndCrossings] = "Red lights and crossings",
        [IncidentGroup.VulnerableRoadUsers] = "Cyclists, pedestrians and riders",
        [IncidentGroup.RoadRage] = "Road rage",
        [IncidentGroup.Collisions] = "Collisions and near misses",
        [IncidentGroup.VehiclesAndDrivers] = "Vehicles and drivers",
        [IncidentGroup.Motorways] = "Motorways",
    };

    /// <summary>Every subcategory, in the order they're listed: group by group, each group's "Other" last.</summary>
    public static readonly IReadOnlyList<IncidentType> All =
    [
        new(IncidentCategory.DisabledBay, IncidentGroup.Parking, "Disabled bay, no badge showing", new VoteQuestion("Should they have parked there?", [new(VoteChoice.No, "No"), new(VoteChoice.Maybe, "Maybe, badge not visible"), new(VoteChoice.Yes, "Yes")])),
        new(IncidentCategory.PavementParking, IncidentGroup.Parking, "Parked on the pavement", new VoteQuestion("Was the pavement blocked?", [new(VoteChoice.Blocked, "Blocked"), new(VoteChoice.PartlyBlocked, "Partly blocked"), new(VoteChoice.RoomToPass, "Room to pass")])),
        new(IncidentCategory.DoubleYellows, IncidentGroup.Parking, "On double yellow lines", new VoteQuestion("Bad parking or fair enough?", [new(VoteChoice.BadParking, "Bad parking"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.SchoolZigZags, IncidentGroup.Parking, "On school zig-zags", new VoteQuestion("Bad parking or fair enough?", [new(VoteChoice.BadParking, "Bad parking"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.BlockingDriveway, IncidentGroup.Parking, "Blocking a driveway or dropped kerb", new VoteQuestion("Bad parking or fair enough?", [new(VoteChoice.BadParking, "Bad parking"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.BusStopOrLane, IncidentGroup.Parking, "In a bus stop or bus lane", new VoteQuestion("Bad parking or fair enough?", [new(VoteChoice.BadParking, "Bad parking"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.ParentAndChildBay, IncidentGroup.Parking, "Parent and child bay, no child", new VoteQuestion("Should they have parked there?", [new(VoteChoice.No, "No"), new(VoteChoice.CantTell, "Can't tell"), new(VoteChoice.Yes, "Yes")])),
        new(IncidentCategory.AcrossTwoBays, IncidentGroup.Parking, "Across two bays", new VoteQuestion("Bad parking or fair enough?", [new(VoteChoice.BadParking, "Bad parking"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.EvChargingBay, IncidentGroup.Parking, "EV charging bay, not charging", new VoteQuestion("Should they have parked there?", [new(VoteChoice.No, "No"), new(VoteChoice.CantTell, "Can't tell"), new(VoteChoice.Yes, "Yes")])),
        new(IncidentCategory.ParkingOther, IncidentGroup.Parking, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.SpeedingInTown, IncidentGroup.Speeding, "Speeding in a 20 or 30", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.SpeedingFastRoad, IncidentGroup.Speeding, "Speeding on a fast road", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.SpeedingSchool, IncidentGroup.Speeding, "Speeding past a school", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.StreetRacing, IncidentGroup.Speeding, "Street racing", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.SpeedingOther, IncidentGroup.Speeding, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.OvertakeIntoOncoming, IncidentGroup.Overtaking, "Into oncoming traffic", new VoteQuestion("Was the overtake safe?", [new(VoteChoice.Safe, "Safe"), new(VoteChoice.Risky, "Risky"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.OvertakeOnBend, IncidentGroup.Overtaking, "On a bend or a crest", new VoteQuestion("Was the overtake safe?", [new(VoteChoice.Safe, "Safe"), new(VoteChoice.Risky, "Risky"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.OvertakeSolidWhite, IncidentGroup.Overtaking, "Over solid white lines", new VoteQuestion("Was the overtake safe?", [new(VoteChoice.Safe, "Safe"), new(VoteChoice.Risky, "Risky"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.OvertakeAtJunction, IncidentGroup.Overtaking, "At a crossing or junction", new VoteQuestion("Was the overtake safe?", [new(VoteChoice.Safe, "Safe"), new(VoteChoice.Risky, "Risky"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.Undertaking, IncidentGroup.Overtaking, "Undertaking", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The undertaker"), new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Nobody, "Nobody")])),
        new(IncidentCategory.OvertakingOther, IncidentGroup.Overtaking, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.PulledOut, IncidentGroup.Junctions, "Pulled out in front", new VoteQuestion("Who had right of way?", [new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.Unclear, "Unclear")])),
        new(IncidentCategory.WrongLaneRoundabout, IncidentGroup.Junctions, "Wrong lane at a roundabout", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Nobody, "Nobody")])),
        new(IncidentCategory.RanStopOrGiveWay, IncidentGroup.Junctions, "Ran a stop or give way sign", new VoteQuestion("Who had right of way?", [new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.Unclear, "Unclear")])),
        new(IncidentCategory.BlockingYellowBox, IncidentGroup.Junctions, "Blocking a yellow box junction", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.CuttingCorner, IncidentGroup.Junctions, "Cutting a corner", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.NoSignal, IncidentGroup.Junctions, "No signal", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.JunctionsOther, IncidentGroup.Junctions, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.MiddleLaneHogging, IncidentGroup.LaneDiscipline, "Middle lane hogging", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.Weaving, IncidentGroup.LaneDiscipline, "Weaving through traffic", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.PushingInAtMerge, IncidentGroup.LaneDiscipline, "Pushing in at a merge", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Nobody, "Nobody")])),
        new(IncidentCategory.StraddlingLanes, IncidentGroup.LaneDiscipline, "Straddling two lanes", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.BusOrCycleLane, IncidentGroup.LaneDiscipline, "Driving in a bus or cycle lane", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.LaneDisciplineOther, IncidentGroup.LaneDiscipline, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.Tailgating, IncidentGroup.FollowingTooClose, "Tailgating", new VoteQuestion("Too close or fine?", [new(VoteChoice.FarTooClose, "Far too close"), new(VoteChoice.TooClose, "Too close"), new(VoteChoice.Fine, "Fine")])),
        new(IncidentCategory.BrakeTesting, IncidentGroup.FollowingTooClose, "Brake testing", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The brake tester"), new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.Both, "Both")])),
        new(IncidentCategory.FollowingOther, IncidentGroup.FollowingTooClose, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.PhoneInHand, IncidentGroup.Distraction, "Phone in hand", new VoteQuestion("Were they distracted?", [new(VoteChoice.Clearly, "Clearly"), new(VoteChoice.Probably, "Probably"), new(VoteChoice.NotReally, "Not really")])),
        new(IncidentCategory.EatingSmokingScreen, IncidentGroup.Distraction, "Eating, smoking or a screen", new VoteQuestion("Were they distracted?", [new(VoteChoice.Clearly, "Clearly"), new(VoteChoice.Probably, "Probably"), new(VoteChoice.NotReally, "Not really")])),
        new(IncidentCategory.NotLooking, IncidentGroup.Distraction, "Not looking", new VoteQuestion("Were they distracted?", [new(VoteChoice.Clearly, "Clearly"), new(VoteChoice.Probably, "Probably"), new(VoteChoice.NotReally, "Not really")])),
        new(IncidentCategory.DistractionOther, IncidentGroup.Distraction, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.RanRedLight, IncidentGroup.RedLightsAndCrossings, "Ran a red light", new VoteQuestion("Did they run it?", [new(VoteChoice.ClearlyRanIt, "Clearly ran it"), new(VoteChoice.AmberGamble, "Amber gamble"), new(VoteChoice.HardToTell, "Hard to tell")])),
        new(IncidentCategory.RanPedestrianCrossing, IncidentGroup.RedLightsAndCrossings, "Ran a pedestrian crossing", new VoteQuestion("Did they run it?", [new(VoteChoice.ClearlyRanIt, "Clearly ran it"), new(VoteChoice.AmberGamble, "Amber gamble"), new(VoteChoice.HardToTell, "Hard to tell")])),
        new(IncidentCategory.LevelCrossing, IncidentGroup.RedLightsAndCrossings, "Level crossing", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.WrongWayOneWay, IncidentGroup.RedLightsAndCrossings, "Wrong way down a one-way street", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.RedLightsOther, IncidentGroup.RedLightsAndCrossings, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.ClosePassCyclist, IncidentGroup.VulnerableRoadUsers, "Close pass of a cyclist", new VoteQuestion("How close was it?", [new(VoteChoice.FarTooClose, "Far too close"), new(VoteChoice.TooClose, "Too close"), new(VoteChoice.GaveEnoughRoom, "Gave enough room")])),
        new(IncidentCategory.ClosePassHorse, IncidentGroup.VulnerableRoadUsers, "Close pass of a horse rider", new VoteQuestion("How close was it?", [new(VoteChoice.FarTooClose, "Far too close"), new(VoteChoice.TooClose, "Too close"), new(VoteChoice.GaveEnoughRoom, "Gave enough room")])),
        new(IncidentCategory.IgnoredPedestrian, IncidentGroup.VulnerableRoadUsers, "Ignored a pedestrian at a crossing", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.DoorOpened, IncidentGroup.VulnerableRoadUsers, "Car door opened into traffic", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The person opening it"), new(VoteChoice.FilmingDriver, "The other road user"), new(VoteChoice.Both, "Both")])),
        new(IncidentCategory.CyclistRidingDangerously, IncidentGroup.VulnerableRoadUsers, "Cyclist riding dangerously", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.PedestrianSteppedOut, IncidentGroup.VulnerableRoadUsers, "Pedestrian stepped out", new VoteQuestion("Who's at fault?", [new(VoteChoice.OtherRoadUser, "The pedestrian"), new(VoteChoice.FilmingDriver, "The driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Nobody, "Nobody")])),
        new(IncidentCategory.VulnerableOther, IncidentGroup.VulnerableRoadUsers, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.ShoutingOrGestures, IncidentGroup.RoadRage, "Shouting or gestures", new VoteQuestion("How serious was it?", [new(VoteChoice.Heated, "Heated"), new(VoteChoice.Aggressive, "Aggressive"), new(VoteChoice.Threatening, "Threatening")])),
        new(IncidentCategory.FollowingOrChasing, IncidentGroup.RoadRage, "Following or chasing", new VoteQuestion("How serious was it?", [new(VoteChoice.Heated, "Heated"), new(VoteChoice.Aggressive, "Aggressive"), new(VoteChoice.Threatening, "Threatening")])),
        new(IncidentCategory.GotOutOfVehicle, IncidentGroup.RoadRage, "Got out of the vehicle", new VoteQuestion("How serious was it?", [new(VoteChoice.Heated, "Heated"), new(VoteChoice.Aggressive, "Aggressive"), new(VoteChoice.Threatening, "Threatening")])),
        new(IncidentCategory.DeliberatelyBlocking, IncidentGroup.RoadRage, "Deliberately blocking", new VoteQuestion("How serious was it?", [new(VoteChoice.Heated, "Heated"), new(VoteChoice.Aggressive, "Aggressive"), new(VoteChoice.Threatening, "Threatening")])),
        new(IncidentCategory.RoadRageOther, IncidentGroup.RoadRage, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.Collision, IncidentGroup.Collisions, "Collision", new VoteQuestion("Who was at fault?", [new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Unclear, "Unclear")])),
        new(IncidentCategory.NearMiss, IncidentGroup.Collisions, "Near miss", new VoteQuestion("Who was at fault?", [new(VoteChoice.FilmingDriver, "The filming driver"), new(VoteChoice.OtherRoadUser, "The other driver"), new(VoteChoice.Both, "Both"), new(VoteChoice.Unclear, "Unclear")])),
        new(IncidentCategory.FailedToStop, IncidentGroup.Collisions, "Failed to stop after a collision", new VoteQuestion("How serious was it?", [new(VoteChoice.Serious, "Serious"), new(VoteChoice.VerySerious, "Very serious")])),
        new(IncidentCategory.LostControl, IncidentGroup.Collisions, "Lost control", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.CollisionsOther, IncidentGroup.Collisions, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.NoLightsAtNight, IncidentGroup.VehiclesAndDrivers, "No lights at night", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.UnsafeLoad, IncidentGroup.VehiclesAndDrivers, "Unsafe or falling load", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.UnroadworthyVehicle, IncidentGroup.VehiclesAndDrivers, "Unroadworthy vehicle", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.LearnerMistake, IncidentGroup.VehiclesAndDrivers, "Learner or new driver mistake", new VoteQuestion("Fair mistake or bad driving?", [new(VoteChoice.FairMistake, "Fair mistake"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.LorryOrBusDriving, IncidentGroup.VehiclesAndDrivers, "Lorry or bus driving", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous")])),
        new(IncidentCategory.VehiclesOther, IncidentGroup.VehiclesAndDrivers, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.ClosedLaneRedX, IncidentGroup.Motorways, "Driving in a closed lane (red X)", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.HardShoulderMisuse, IncidentGroup.Motorways, "Misusing the hard shoulder", new VoteQuestion("Bad driving or fair enough?", [new(VoteChoice.BadDriving, "Bad driving"), new(VoteChoice.FairEnough, "Fair enough")])),
        new(IncidentCategory.StoppingOnMotorway, IncidentGroup.Motorways, "Stopping or reversing on a motorway", new VoteQuestion("How bad was it?", [new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.WrongWayDriver, IncidentGroup.Motorways, "Wrong-way driver", new VoteQuestion("How bad was it?", [new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
        new(IncidentCategory.MotorwaysOther, IncidentGroup.Motorways, "Other", new VoteQuestion("How bad was it?", [new(VoteChoice.Minor, "Minor"), new(VoteChoice.Careless, "Careless"), new(VoteChoice.Dangerous, "Dangerous"), new(VoteChoice.Shocking, "Shocking")])),
    ];

    private static readonly IReadOnlyDictionary<IncidentCategory, IncidentType> ByCategory = All.ToDictionary(t => t.Category);

    /// <summary>The groups, in the order they're listed.</summary>
    public static IReadOnlyList<IncidentGroup> Groups { get; } = Enum.GetValues<IncidentGroup>();

    public static IncidentType Of(IncidentCategory category) => ByCategory[category];

    public static IncidentGroup GroupOf(IncidentCategory category) => ByCategory[category].Group;

    public static string Label(IncidentCategory category) => ByCategory[category].Label;

    public static string Label(IncidentGroup group) => GroupLabels[group];

    /// <summary>The subcategories in a group, its "Other" last.</summary>
    public static IReadOnlyList<IncidentCategory> In(IncidentGroup group) =>
        All.Where(t => t.Group == group).Select(t => t.Category).ToList();

    /// <summary>The vote a post with this main subcategory asks.</summary>
    public static VoteQuestion VoteFor(IncidentCategory main) => ByCategory[main].Vote;

    /// <summary>
    /// Subcategories where other people can be in the reporter's vehicle: everything except Parking (used for passenger
    /// tags).
    /// </summary>
    public static bool IsOnTheRoad(IncidentCategory category) => GroupOf(category) != IncidentGroup.Parking;
}
