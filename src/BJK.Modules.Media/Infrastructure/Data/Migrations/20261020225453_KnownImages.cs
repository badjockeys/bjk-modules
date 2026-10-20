using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class KnownImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "NudityScore",
                schema: "media",
                table: "Media",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerceptualHashes",
                schema: "media",
                table: "Media",
                type: "nvarchar(520)",
                maxLength: 520,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KnownImageHashes",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Value = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownImageHashes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnownImageMatches",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HashId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MatchedValue = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Distance = table.Column<int>(type: "int", nullable: true),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    MatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReportReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownImageMatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownImageHashes_Source_Kind_Value",
                schema: "media",
                table: "KnownImageHashes",
                columns: new[] { "Source", "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnownImageMatches_MatchedAt",
                schema: "media",
                table: "KnownImageMatches",
                column: "MatchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_KnownImageMatches_MediaId",
                schema: "media",
                table: "KnownImageMatches",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_KnownImageMatches_UserId",
                schema: "media",
                table: "KnownImageMatches",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownImageHashes",
                schema: "media");

            migrationBuilder.DropTable(
                name: "KnownImageMatches",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "NudityScore",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "PerceptualHashes",
                schema: "media",
                table: "Media");
        }
    }
}
