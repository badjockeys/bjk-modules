using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VehicleDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VehicleFrames",
                schema: "media",
                table: "VideoSegments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VehicleFrames",
                schema: "media",
                table: "Media",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VehicleFrames",
                schema: "media",
                table: "VideoSegments");

            migrationBuilder.DropColumn(
                name: "VehicleFrames",
                schema: "media",
                table: "Media");
        }
    }
}
