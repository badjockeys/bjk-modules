namespace BJK.Modules.Posts.Domain;

/// <summary>
/// The 13 groups the types of bad driving are listed under (owner signed off the categories doc, 23 Oct 2026). Stored by
/// name.
/// </summary>
public enum IncidentGroup
{
    /// <summary>Parking.</summary>
    Parking,

    /// <summary>Speeding.</summary>
    Speeding,

    /// <summary>Overtaking.</summary>
    Overtaking,

    /// <summary>Junctions and roundabouts.</summary>
    Junctions,

    /// <summary>Lane discipline.</summary>
    LaneDiscipline,

    /// <summary>Following too close.</summary>
    FollowingTooClose,

    /// <summary>Distraction.</summary>
    Distraction,

    /// <summary>Red lights and crossings.</summary>
    RedLightsAndCrossings,

    /// <summary>Cyclists, pedestrians and riders.</summary>
    VulnerableRoadUsers,

    /// <summary>Road rage.</summary>
    RoadRage,

    /// <summary>Collisions and near misses.</summary>
    Collisions,

    /// <summary>Vehicles and drivers.</summary>
    VehiclesAndDrivers,

    /// <summary>Motorways.</summary>
    Motorways,
}

/// <summary>
/// What kind of bad driving a post shows: one of the subcategories in <see cref="IncidentTaxonomy"/>, each in an
/// <see cref="IncidentGroup"/>, every group with its own "Other". Stored by name. Values are bit positions in
/// <c>TypeFlags</c> / <c>TypeFlagsHigh</c> (see <see cref="IncidentTypeFlags"/>): add new ones at the end, never reorder.
/// </summary>
public enum IncidentCategory
{
    /// <summary>Disabled bay, no badge showing (Parking).</summary>
    DisabledBay,

    /// <summary>Parked on the pavement (Parking).</summary>
    PavementParking,

    /// <summary>On double yellow lines (Parking).</summary>
    DoubleYellows,

    /// <summary>On school zig-zags (Parking).</summary>
    SchoolZigZags,

    /// <summary>Blocking a driveway or dropped kerb (Parking).</summary>
    BlockingDriveway,

    /// <summary>In a bus stop or bus lane (Parking).</summary>
    BusStopOrLane,

    /// <summary>Parent and child bay, no child (Parking).</summary>
    ParentAndChildBay,

    /// <summary>Across two bays (Parking).</summary>
    AcrossTwoBays,

    /// <summary>EV charging bay, not charging (Parking).</summary>
    EvChargingBay,

    /// <summary>Other (Parking).</summary>
    ParkingOther,

    /// <summary>Speeding in a 20 or 30 (Speeding).</summary>
    SpeedingInTown,

    /// <summary>Speeding on a fast road (Speeding).</summary>
    SpeedingFastRoad,

    /// <summary>Speeding past a school (Speeding).</summary>
    SpeedingSchool,

    /// <summary>Street racing (Speeding).</summary>
    StreetRacing,

    /// <summary>Other (Speeding).</summary>
    SpeedingOther,

    /// <summary>Into oncoming traffic (Overtaking).</summary>
    OvertakeIntoOncoming,

    /// <summary>On a bend or a crest (Overtaking).</summary>
    OvertakeOnBend,

    /// <summary>Over solid white lines (Overtaking).</summary>
    OvertakeSolidWhite,

    /// <summary>At a crossing or junction (Overtaking).</summary>
    OvertakeAtJunction,

    /// <summary>Undertaking (Overtaking).</summary>
    Undertaking,

    /// <summary>Other (Overtaking).</summary>
    OvertakingOther,

    /// <summary>Pulled out in front (Junctions and roundabouts).</summary>
    PulledOut,

    /// <summary>Wrong lane at a roundabout (Junctions and roundabouts).</summary>
    WrongLaneRoundabout,

    /// <summary>Ran a stop or give way sign (Junctions and roundabouts).</summary>
    RanStopOrGiveWay,

    /// <summary>Blocking a yellow box junction (Junctions and roundabouts).</summary>
    BlockingYellowBox,

    /// <summary>Cutting a corner (Junctions and roundabouts).</summary>
    CuttingCorner,

    /// <summary>No signal (Junctions and roundabouts).</summary>
    NoSignal,

    /// <summary>Other (Junctions and roundabouts).</summary>
    JunctionsOther,

    /// <summary>Middle lane hogging (Lane discipline).</summary>
    MiddleLaneHogging,

    /// <summary>Weaving through traffic (Lane discipline).</summary>
    Weaving,

    /// <summary>Pushing in at a merge (Lane discipline).</summary>
    PushingInAtMerge,

    /// <summary>Straddling two lanes (Lane discipline).</summary>
    StraddlingLanes,

    /// <summary>Driving in a bus or cycle lane (Lane discipline).</summary>
    BusOrCycleLane,

    /// <summary>Other (Lane discipline).</summary>
    LaneDisciplineOther,

    /// <summary>Tailgating (Following too close).</summary>
    Tailgating,

    /// <summary>Brake testing (Following too close).</summary>
    BrakeTesting,

    /// <summary>Other (Following too close).</summary>
    FollowingOther,

    /// <summary>Phone in hand (Distraction).</summary>
    PhoneInHand,

    /// <summary>Eating, smoking or a screen (Distraction).</summary>
    EatingSmokingScreen,

    /// <summary>Not looking (Distraction).</summary>
    NotLooking,

    /// <summary>Other (Distraction).</summary>
    DistractionOther,

    /// <summary>Ran a red light (Red lights and crossings).</summary>
    RanRedLight,

    /// <summary>Ran a pedestrian crossing (Red lights and crossings).</summary>
    RanPedestrianCrossing,

    /// <summary>Level crossing (Red lights and crossings).</summary>
    LevelCrossing,

    /// <summary>Wrong way down a one-way street (Red lights and crossings).</summary>
    WrongWayOneWay,

    /// <summary>Other (Red lights and crossings).</summary>
    RedLightsOther,

    /// <summary>Close pass of a cyclist (Cyclists, pedestrians and riders).</summary>
    ClosePassCyclist,

    /// <summary>Close pass of a horse rider (Cyclists, pedestrians and riders).</summary>
    ClosePassHorse,

    /// <summary>Ignored a pedestrian at a crossing (Cyclists, pedestrians and riders).</summary>
    IgnoredPedestrian,

    /// <summary>Car door opened into traffic (Cyclists, pedestrians and riders).</summary>
    DoorOpened,

    /// <summary>Cyclist riding dangerously (Cyclists, pedestrians and riders).</summary>
    CyclistRidingDangerously,

    /// <summary>Pedestrian stepped out (Cyclists, pedestrians and riders).</summary>
    PedestrianSteppedOut,

    /// <summary>Other (Cyclists, pedestrians and riders).</summary>
    VulnerableOther,

    /// <summary>Shouting or gestures (Road rage).</summary>
    ShoutingOrGestures,

    /// <summary>Following or chasing (Road rage).</summary>
    FollowingOrChasing,

    /// <summary>Got out of the vehicle (Road rage).</summary>
    GotOutOfVehicle,

    /// <summary>Deliberately blocking (Road rage).</summary>
    DeliberatelyBlocking,

    /// <summary>Other (Road rage).</summary>
    RoadRageOther,

    /// <summary>Collision (Collisions and near misses).</summary>
    Collision,

    /// <summary>Near miss (Collisions and near misses).</summary>
    NearMiss,

    /// <summary>Failed to stop after a collision (Collisions and near misses).</summary>
    FailedToStop,

    /// <summary>Lost control (Collisions and near misses).</summary>
    LostControl,

    /// <summary>Other (Collisions and near misses).</summary>
    CollisionsOther,

    /// <summary>No lights at night (Vehicles and drivers).</summary>
    NoLightsAtNight,

    /// <summary>Unsafe or falling load (Vehicles and drivers).</summary>
    UnsafeLoad,

    /// <summary>Unroadworthy vehicle (Vehicles and drivers).</summary>
    UnroadworthyVehicle,

    /// <summary>Learner or new driver mistake (Vehicles and drivers).</summary>
    LearnerMistake,

    /// <summary>Lorry or bus driving (Vehicles and drivers).</summary>
    LorryOrBusDriving,

    /// <summary>Other (Vehicles and drivers).</summary>
    VehiclesOther,

    /// <summary>Driving in a closed lane (red X) (Motorways).</summary>
    ClosedLaneRedX,

    /// <summary>Misusing the hard shoulder (Motorways).</summary>
    HardShoulderMisuse,

    /// <summary>Stopping or reversing on a motorway (Motorways).</summary>
    StoppingOnMotorway,

    /// <summary>Wrong-way driver (Motorways).</summary>
    WrongWayDriver,

    /// <summary>Other (Motorways).</summary>
    MotorwaysOther,
}
