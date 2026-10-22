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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsMinor",
                schema: "profiles",
                table: "Profiles");
        }
    }
}
