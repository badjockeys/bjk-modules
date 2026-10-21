using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipFrames",
                schema: "media",
                table: "VideoSegments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "ClipSummary",
                schema: "media",
                table: "VideoSegments",
                type: "varbinary(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ClipSummary",
                schema: "media",
                table: "Media",
                type: "varbinary(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Descriptions",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaIds = table.Column<string>(type: "varchar(400)", unicode: false, maxLength: 400, nullable: false),
                    Context = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Descriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Descriptions_UserId_CreatedAt",
                schema: "media",
                table: "Descriptions",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Descriptions",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "ClipFrames",
                schema: "media",
                table: "VideoSegments");

            migrationBuilder.DropColumn(
                name: "ClipSummary",
                schema: "media",
                table: "VideoSegments");

            migrationBuilder.DropColumn(
                name: "ClipSummary",
                schema: "media",
                table: "Media");
        }
    }
}
