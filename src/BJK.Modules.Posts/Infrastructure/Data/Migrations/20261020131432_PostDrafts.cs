using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Drafts",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TypeFlags = table.Column<int>(type: "int", nullable: false),
                    IncidentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    PlaceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    Passengers = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VehicleType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    VehicleMake = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    VehicleModel = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    VehicleColour = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ReportedToPolice = table.Column<bool>(type: "bit", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DraftMedia",
                schema: "posts",
                columns: table => new
                {
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftMedia", x => new { x.DraftId, x.MediaId });
                    table.ForeignKey(
                        name: "FK_DraftMedia_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalSchema: "posts",
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftMedia_MediaId",
                schema: "posts",
                table: "DraftMedia",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_AuthorId_UpdatedAt",
                schema: "posts",
                table: "Drafts",
                columns: new[] { "AuthorId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_UpdatedAt",
                schema: "posts",
                table: "Drafts",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DraftMedia",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "Drafts",
                schema: "posts");
        }
    }
}
