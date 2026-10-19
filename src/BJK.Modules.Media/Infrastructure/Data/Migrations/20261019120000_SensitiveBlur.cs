using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Sensitive media shown blurred: which media are sensitive and who marked them (media.SensitiveMedia: its post's
    /// tag or automatic detection, with the score), and on media.Media whether the processed files are kept privately
    /// (IsRestricted), the key of the blurred public preview (PreviewKey) and the graphic content score
    /// (SensitiveScore, also per video segment). Only additions; nothing is changed or removed, and existing media stay public.
    /// </summary>
    public partial class SensitiveBlur : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRestricted",
                schema: "media",
                table: "Media",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreviewKey",
                schema: "media",
                table: "Media",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SensitiveScore",
                schema: "media",
                table: "Media",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SensitiveScore",
                schema: "media",
                table: "VideoSegments",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SensitiveMedia",
                schema: "media",
                columns: table => new
                {
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MarkedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Score = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SensitiveMedia", x => x.MediaId);
                    table.ForeignKey(
                        name: "FK_SensitiveMedia_Media_MediaId",
                        column: x => x.MediaId,
                        principalSchema: "media",
                        principalTable: "Media",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SensitiveMedia",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "IsRestricted",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "PreviewKey",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "SensitiveScore",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "SensitiveScore",
                schema: "media",
                table: "VideoSegments");
        }
    }
}
