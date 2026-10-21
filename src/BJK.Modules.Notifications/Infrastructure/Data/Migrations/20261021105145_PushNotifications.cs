using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PushNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Push",
                schema: "notifications",
                table: "NotificationSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                schema: "notifications",
                table: "Notifications",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPushedAt",
                schema: "notifications",
                table: "Notifications",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                schema: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Auth = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                schema: "notifications",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId",
                schema: "notifications",
                table: "PushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushSubscriptions",
                schema: "notifications");

            migrationBuilder.DropColumn(
                name: "Push",
                schema: "notifications",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "Detail",
                schema: "notifications",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "LastPushedAt",
                schema: "notifications",
                table: "Notifications");
        }
    }
}
