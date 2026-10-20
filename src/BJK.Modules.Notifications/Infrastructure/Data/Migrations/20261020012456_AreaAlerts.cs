using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AreaAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AreaAlertItems",
                schema: "notifications",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PlaceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AreaAlertItems", x => new { x.UserId, x.PostId });
                });

            migrationBuilder.CreateTable(
                name: "AreaSubscriptions",
                schema: "notifications",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlaceName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    RadiusKm = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Email = table.Column<bool>(type: "bit", nullable: false),
                    IsPremium = table.Column<bool>(type: "bit", nullable: false),
                    PlanRecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentDay = table.Column<DateOnly>(type: "date", nullable: true),
                    SentToday = table.Column<int>(type: "int", nullable: false),
                    LastSummaryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AreaSubscriptions", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AreaAlertItems_CreatedAt",
                schema: "notifications",
                table: "AreaAlertItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AreaSubscriptions_Active",
                schema: "notifications",
                table: "AreaSubscriptions",
                columns: new[] { "Latitude", "Longitude" },
                filter: "[Enabled] = 1 AND [IsPremium] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AreaAlertItems",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "AreaSubscriptions",
                schema: "notifications");
        }
    }
}
