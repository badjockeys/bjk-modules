using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FastPosting : Migration
    {
        /// <summary>
        /// Media events (media.Outbox) and segment progress for videos processed in parts (media.VideoJobs,
        /// media.VideoSegments). Only new tables; nothing is changed or removed. (The redaction columns on media.Media
        /// come from the MediaRedaction migration.)
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VideoJobs",
                schema: "media",
                columns: table => new
                {
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    FrameRate = table.Column<double>(type: "float", nullable: false),
                    FramesPerSegment = table.Column<int>(type: "int", nullable: false),
                    OverlapFrames = table.Column<int>(type: "int", nullable: false),
                    FirstFrame = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssembleQueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AssembledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoJobs", x => new { x.MediaId, x.Revision });
                    table.ForeignKey(
                        name: "FK_VideoJobs_Media_MediaId",
                        column: x => x.MediaId,
                        principalSchema: "media",
                        principalTable: "Media",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VideoSegments",
                schema: "media",
                columns: table => new
                {
                    MediaId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    StartFrame = table.Column<int>(type: "int", nullable: false),
                    EndFrame = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Frames = table.Column<int>(type: "int", nullable: true),
                    FacesBlurred = table.Column<int>(type: "int", nullable: true),
                    PlatesBlurred = table.Column<int>(type: "int", nullable: true),
                    ReviewReasons = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ElapsedMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoSegments", x => new { x.MediaId, x.Revision, x.Index });
                    table.ForeignKey(
                        name: "FK_VideoSegments_VideoJobs_MediaId_Revision",
                        columns: x => new { x.MediaId, x.Revision },
                        principalSchema: "media",
                        principalTable: "VideoJobs",
                        principalColumns: new[] { "MediaId", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_PublishedAt_OccurredAt",
                schema: "media",
                table: "Outbox",
                columns: new[] { "PublishedAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "media");

            migrationBuilder.DropTable(
                name: "VideoSegments",
                schema: "media");

            migrationBuilder.DropTable(
                name: "VideoJobs",
                schema: "media");
        }
    }
}
