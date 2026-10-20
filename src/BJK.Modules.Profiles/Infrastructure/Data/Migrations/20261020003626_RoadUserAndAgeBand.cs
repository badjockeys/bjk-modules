using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Profiles.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoadUserAndAgeBand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMinor",
                schema: "profiles",
                table: "Profiles",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoadUser",
                schema: "profiles",
                table: "Profiles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowRoadUser",
                schema: "profiles",
                table: "Profiles",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsMinor",
                schema: "profiles",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "RoadUser",
                schema: "profiles",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "ShowRoadUser",
                schema: "profiles",
                table: "Profiles");
        }
    }
}
