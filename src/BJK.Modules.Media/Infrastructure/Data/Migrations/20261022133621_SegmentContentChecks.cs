using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SegmentContentChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "NudityScore",
                schema: "media",
                table: "VideoSegments",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerceptualHashes",
                schema: "media",
                table: "VideoSegments",
                type: "nvarchar(520)",
                maxLength: 520,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NudityScore",
                schema: "media",
                table: "VideoSegments");

            migrationBuilder.DropColumn(
                name: "PerceptualHashes",
                schema: "media",
                table: "VideoSegments");
        }
    }
}
