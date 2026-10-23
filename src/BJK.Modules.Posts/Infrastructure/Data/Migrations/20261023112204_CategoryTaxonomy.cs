using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CategoryTaxonomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.AlterColumn<long>(
                name: "TypeFlags",
                schema: "posts",
                table: "Timelines",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<long>(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Timelines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "TypeFlags",
                schema: "posts",
                table: "Posts",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Posts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "TypeFlags",
                schema: "posts",
                table: "Drafts",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<long>(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Drafts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing posts, drafts and timelines move to the new subcategories (the categories doc's "What happens to
            // existing posts"): flags first (they still hold the old 12 bits), then names, then each post's group.
            // In SQL Server, SET uses the row's values from before the UPDATE, so the high half reads the old bits.
            migrationBuilder.Sql(@"UPDATE [posts].[Posts] SET [TypeFlagsHigh] = CASE WHEN [TypeFlags] & 2048 <> 0 THEN CAST(16 AS bigint) ELSE 0 END, [TypeFlags] = CASE WHEN [TypeFlags] & 1 <> 0 THEN CAST(9007199254740992 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 2 <> 0 THEN CAST(32768 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 4 <> 0 THEN CAST(17179869184 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 8 <> 0 THEN CAST(137438953472 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 16 <> 0 THEN CAST(2048 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 32 <> 0 THEN CAST(2199023255552 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 64 <> 0 THEN CAST(1073741824 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 128 <> 0 THEN CAST(524288 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 256 <> 0 THEN CAST(268435456 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 512 <> 0 THEN CAST(70368744177664 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 1024 <> 0 THEN CAST(512 AS bigint) ELSE 0 END;");
            migrationBuilder.Sql(@"UPDATE [posts].[Drafts] SET [TypeFlagsHigh] = CASE WHEN [TypeFlags] & 2048 <> 0 THEN CAST(16 AS bigint) ELSE 0 END, [TypeFlags] = CASE WHEN [TypeFlags] & 1 <> 0 THEN CAST(9007199254740992 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 2 <> 0 THEN CAST(32768 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 4 <> 0 THEN CAST(17179869184 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 8 <> 0 THEN CAST(137438953472 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 16 <> 0 THEN CAST(2048 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 32 <> 0 THEN CAST(2199023255552 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 64 <> 0 THEN CAST(1073741824 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 128 <> 0 THEN CAST(524288 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 256 <> 0 THEN CAST(268435456 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 512 <> 0 THEN CAST(70368744177664 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 1024 <> 0 THEN CAST(512 AS bigint) ELSE 0 END;");
            migrationBuilder.Sql(@"UPDATE [posts].[Timelines] SET [TypeFlagsHigh] = CASE WHEN [TypeFlags] & 2048 <> 0 THEN CAST(16 AS bigint) ELSE 0 END, [TypeFlags] = CASE WHEN [TypeFlags] & 1 <> 0 THEN CAST(9007199254740992 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 2 <> 0 THEN CAST(32768 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 4 <> 0 THEN CAST(17179869184 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 8 <> 0 THEN CAST(137438953472 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 16 <> 0 THEN CAST(2048 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 32 <> 0 THEN CAST(2199023255552 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 64 <> 0 THEN CAST(1073741824 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 128 <> 0 THEN CAST(524288 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 256 <> 0 THEN CAST(268435456 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 512 <> 0 THEN CAST(70368744177664 AS bigint) ELSE 0 END | CASE WHEN [TypeFlags] & 1024 <> 0 THEN CAST(512 AS bigint) ELSE 0 END;");
            migrationBuilder.Sql(@"UPDATE [posts].[Posts] SET [Category] = CASE [Category] WHEN N'RoadRage' THEN N'ShoutingOrGestures' WHEN N'DangerousOvertaking' THEN N'OvertakeIntoOncoming' WHEN N'Tailgating' THEN N'Tailgating' WHEN N'MobilePhoneUse' THEN N'PhoneInHand' WHEN N'Speeding' THEN N'SpeedingFastRoad' WHEN N'RedLightRunning' THEN N'RanRedLight' WHEN N'CuttingUp' THEN N'PushingInAtMerge' WHEN N'Undertaking' THEN N'Undertaking' WHEN N'LaneHogging' THEN N'MiddleLaneHogging' WHEN N'ClosePass' THEN N'ClosePassCyclist' WHEN N'BadParking' THEN N'ParkingOther' WHEN N'Other' THEN N'VehiclesOther' ELSE [Category] END;");
            migrationBuilder.Sql(@"UPDATE [posts].[Drafts] SET [Category] = CASE [Category] WHEN N'RoadRage' THEN N'ShoutingOrGestures' WHEN N'DangerousOvertaking' THEN N'OvertakeIntoOncoming' WHEN N'Tailgating' THEN N'Tailgating' WHEN N'MobilePhoneUse' THEN N'PhoneInHand' WHEN N'Speeding' THEN N'SpeedingFastRoad' WHEN N'RedLightRunning' THEN N'RanRedLight' WHEN N'CuttingUp' THEN N'PushingInAtMerge' WHEN N'Undertaking' THEN N'Undertaking' WHEN N'LaneHogging' THEN N'MiddleLaneHogging' WHEN N'ClosePass' THEN N'ClosePassCyclist' WHEN N'BadParking' THEN N'ParkingOther' WHEN N'Other' THEN N'VehiclesOther' ELSE [Category] END WHERE [Category] IS NOT NULL;");
            migrationBuilder.Sql(@"UPDATE [posts].[Posts] SET [Group] = CASE [Category] WHEN N'DisabledBay' THEN N'Parking' WHEN N'PavementParking' THEN N'Parking' WHEN N'DoubleYellows' THEN N'Parking' WHEN N'SchoolZigZags' THEN N'Parking' WHEN N'BlockingDriveway' THEN N'Parking' WHEN N'BusStopOrLane' THEN N'Parking' WHEN N'ParentAndChildBay' THEN N'Parking' WHEN N'AcrossTwoBays' THEN N'Parking' WHEN N'EvChargingBay' THEN N'Parking' WHEN N'ParkingOther' THEN N'Parking' WHEN N'SpeedingInTown' THEN N'Speeding' WHEN N'SpeedingFastRoad' THEN N'Speeding' WHEN N'SpeedingSchool' THEN N'Speeding' WHEN N'StreetRacing' THEN N'Speeding' WHEN N'SpeedingOther' THEN N'Speeding' WHEN N'OvertakeIntoOncoming' THEN N'Overtaking' WHEN N'OvertakeOnBend' THEN N'Overtaking' WHEN N'OvertakeSolidWhite' THEN N'Overtaking' WHEN N'OvertakeAtJunction' THEN N'Overtaking' WHEN N'Undertaking' THEN N'Overtaking' WHEN N'OvertakingOther' THEN N'Overtaking' WHEN N'PulledOut' THEN N'Junctions' WHEN N'WrongLaneRoundabout' THEN N'Junctions' WHEN N'RanStopOrGiveWay' THEN N'Junctions' WHEN N'BlockingYellowBox' THEN N'Junctions' WHEN N'CuttingCorner' THEN N'Junctions' WHEN N'NoSignal' THEN N'Junctions' WHEN N'JunctionsOther' THEN N'Junctions' WHEN N'MiddleLaneHogging' THEN N'LaneDiscipline' WHEN N'Weaving' THEN N'LaneDiscipline' WHEN N'PushingInAtMerge' THEN N'LaneDiscipline' WHEN N'StraddlingLanes' THEN N'LaneDiscipline' WHEN N'BusOrCycleLane' THEN N'LaneDiscipline' WHEN N'LaneDisciplineOther' THEN N'LaneDiscipline' WHEN N'Tailgating' THEN N'FollowingTooClose' WHEN N'BrakeTesting' THEN N'FollowingTooClose' WHEN N'FollowingOther' THEN N'FollowingTooClose' WHEN N'PhoneInHand' THEN N'Distraction' WHEN N'EatingSmokingScreen' THEN N'Distraction' WHEN N'NotLooking' THEN N'Distraction' WHEN N'DistractionOther' THEN N'Distraction' WHEN N'RanRedLight' THEN N'RedLightsAndCrossings' WHEN N'RanPedestrianCrossing' THEN N'RedLightsAndCrossings' WHEN N'LevelCrossing' THEN N'RedLightsAndCrossings' WHEN N'WrongWayOneWay' THEN N'RedLightsAndCrossings' WHEN N'RedLightsOther' THEN N'RedLightsAndCrossings' WHEN N'ClosePassCyclist' THEN N'VulnerableRoadUsers' WHEN N'ClosePassHorse' THEN N'VulnerableRoadUsers' WHEN N'IgnoredPedestrian' THEN N'VulnerableRoadUsers' WHEN N'DoorOpened' THEN N'VulnerableRoadUsers' WHEN N'CyclistRidingDangerously' THEN N'VulnerableRoadUsers' WHEN N'PedestrianSteppedOut' THEN N'VulnerableRoadUsers' WHEN N'VulnerableOther' THEN N'VulnerableRoadUsers' WHEN N'ShoutingOrGestures' THEN N'RoadRage' WHEN N'FollowingOrChasing' THEN N'RoadRage' WHEN N'GotOutOfVehicle' THEN N'RoadRage' WHEN N'DeliberatelyBlocking' THEN N'RoadRage' WHEN N'RoadRageOther' THEN N'RoadRage' WHEN N'Collision' THEN N'Collisions' WHEN N'NearMiss' THEN N'Collisions' WHEN N'FailedToStop' THEN N'Collisions' WHEN N'LostControl' THEN N'Collisions' WHEN N'CollisionsOther' THEN N'Collisions' WHEN N'NoLightsAtNight' THEN N'VehiclesAndDrivers' WHEN N'UnsafeLoad' THEN N'VehiclesAndDrivers' WHEN N'UnroadworthyVehicle' THEN N'VehiclesAndDrivers' WHEN N'LearnerMistake' THEN N'VehiclesAndDrivers' WHEN N'LorryOrBusDriving' THEN N'VehiclesAndDrivers' WHEN N'VehiclesOther' THEN N'VehiclesAndDrivers' WHEN N'ClosedLaneRedX' THEN N'Motorways' WHEN N'HardShoulderMisuse' THEN N'Motorways' WHEN N'StoppingOnMotorway' THEN N'Motorways' WHEN N'WrongWayDriver' THEN N'Motorways' WHEN N'MotorwaysOther' THEN N'Motorways' ELSE N'VehiclesAndDrivers' END;");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "TypeFlags", "TypeFlagsHigh", "PlaceName", "VehicleType", "VehicleMake", "VehicleModel", "VehicleColour", "IsDark", "ReportedToPolice", "HasVideo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Timelines");

            migrationBuilder.DropColumn(
                name: "Group",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TypeFlagsHigh",
                schema: "posts",
                table: "Drafts");

            migrationBuilder.AlterColumn<int>(
                name: "TypeFlags",
                schema: "posts",
                table: "Timelines",
                type: "int",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<int>(
                name: "TypeFlags",
                schema: "posts",
                table: "Posts",
                type: "int",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<int>(
                name: "TypeFlags",
                schema: "posts",
                table: "Drafts",
                type: "int",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "TypeFlags", "PlaceName", "VehicleType", "VehicleMake", "VehicleModel", "VehicleColour", "IsDark", "ReportedToPolice", "HasVideo" });
        }
    }
}
