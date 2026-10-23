using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Notifications.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Link",
                schema: "notifications",
                table: "Notifications",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Link",
                schema: "notifications",
                table: "Notifications");
        }
    }
}
