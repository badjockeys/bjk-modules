using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VehicleDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.AddColumn<bool>(
                name: "IsDark",
                schema: "posts",
                table: "Posts",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReportedToPolice",
                schema: "posts",
                table: "Posts",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleColour",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleMake",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleModel",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VehicleType",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "TypeFlags", "PlaceName", "VehicleType", "VehicleMake", "VehicleModel", "VehicleColour", "IsDark", "ReportedToPolice", "HasVideo" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "IsDark",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ReportedToPolice",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VehicleColour",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VehicleMake",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VehicleModel",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VehicleType",
                schema: "posts",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "TypeFlags", "PlaceName" });
        }
    }
}
