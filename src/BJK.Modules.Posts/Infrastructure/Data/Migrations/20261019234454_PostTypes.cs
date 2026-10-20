using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.AddColumn<int>(
                name: "TypeFlags",
                schema: "posts",
                table: "Posts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Existing posts have one type: their category (bit n for IncidentCategory value n).
            migrationBuilder.Sql(
                "UPDATE [posts].[Posts] SET [TypeFlags] = CASE [Category] WHEN N'RoadRage' THEN 1 WHEN N'DangerousOvertaking' THEN 2 WHEN N'Tailgating' THEN 4 WHEN N'MobilePhoneUse' THEN 8 WHEN N'Speeding' THEN 16 WHEN N'RedLightRunning' THEN 32 WHEN N'CuttingUp' THEN 64 WHEN N'Undertaking' THEN 128 WHEN N'LaneHogging' THEN 256 WHEN N'ClosePass' THEN 512 WHEN N'BadParking' THEN 1024 WHEN N'Other' THEN 2048 ELSE 0 END WHERE [TypeFlags] = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "TypeFlags", "PlaceName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "TypeFlags",
                schema: "posts",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "PlaceName" });
        }
    }
}
