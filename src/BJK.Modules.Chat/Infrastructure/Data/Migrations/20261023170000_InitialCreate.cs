using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Chat.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "chat");

            migrationBuilder.CreateTable(
                name: "DailyUsage",
                schema: "chat",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Questions = table.Column<int>(type: "int", nullable: false),
                    Strikes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyUsage", x => new { x.UserId, x.Day });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyUsage",
                schema: "chat");
        }
    }
}
