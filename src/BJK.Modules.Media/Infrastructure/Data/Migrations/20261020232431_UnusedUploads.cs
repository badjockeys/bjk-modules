using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnusedUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UsageCheckedAt",
                schema: "media",
                table: "Media",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Media_UnusedUploads",
                schema: "media",
                table: "Media",
                column: "CreatedAt",
                filter: "[UsageCheckedAt] IS NULL AND [Status] <> 'Deleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Media_UnusedUploads",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "UsageCheckedAt",
                schema: "media",
                table: "Media");
        }
    }
}
