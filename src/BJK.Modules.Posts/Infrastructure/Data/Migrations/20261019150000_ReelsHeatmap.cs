using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Reels and the incident heat map: <c>HasVideo</c> and <c>ReelScore</c> on posts (existing video posts are found
    /// from the Media module's table once, here, if it's in the same database), reel plays and daily play counts, and
    /// filtered indexes for reels and the heat map. Only additions; nothing is changed or removed.
    /// </summary>
    public partial class ReelsHeatmap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasVideo",
                schema: "posts",
                table: "Posts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "ReelScore",
                schema: "posts",
                table: "Posts",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            // Existing video posts. A one-off read of the Media module's table, in the same database (badjockeys); the
            // module code never reads it. Skipped if it isn't there (e.g. a test database with only this module).
            // The reels job scores them within minutes of startup.
            migrationBuilder.Sql(
                """
                IF OBJECT_ID(N'[media].[Media]', N'U') IS NOT NULL
                    EXEC(N'UPDATE [p] SET [HasVideo] = 1
                        FROM [posts].[Posts] AS [p]
                        WHERE EXISTS (
                            SELECT 1 FROM [posts].[PostMedia] AS [pm]
                            JOIN [media].[Media] AS [m] ON [m].[Id] = [pm].[MediaId]
                            WHERE [pm].[PostId] = [p].[Id] AND [m].[MediaType] = N''Video'');');
                """);

            migrationBuilder.CreateTable(
                name: "ReelPlayDailyCounts",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Plays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReelPlayDailyCounts", x => new { x.PostId, x.Day });
                    table.ForeignKey(
                        name: "FK_ReelPlayDailyCounts_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReelPlays",
                schema: "posts",
                columns: table => new
                {
                    ViewerKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsAnonymous = table.Column<bool>(type: "bit", nullable: false),
                    PlayedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReelPlays", x => new { x.ViewerKey, x.Day, x.PostId });
                    table.ForeignKey(
                        name: "FK_ReelPlays_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts",
                column: "IncidentAt",
                filter: "[Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0")
                .Annotation("SqlServer:Include", new[] { "Latitude", "Longitude", "Category", "PlaceName" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Reels",
                schema: "posts",
                table: "Posts",
                columns: new[] { "ReelScore", "CreatedAt" },
                filter: "[HasVideo] = 1 AND [Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Reels_Category",
                schema: "posts",
                table: "Posts",
                columns: new[] { "Category", "ReelScore", "CreatedAt" },
                filter: "[HasVideo] = 1 AND [Status] = N'Published' AND [MediaState] = N'Ready' AND [IsAuthorDeactivated] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ReelPlays_Day_IsAnonymous",
                schema: "posts",
                table: "ReelPlays",
                columns: new[] { "Day", "IsAnonymous" });

            migrationBuilder.CreateIndex(
                name: "IX_ReelPlays_PostId",
                schema: "posts",
                table: "ReelPlays",
                column: "PostId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReelPlayDailyCounts",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "ReelPlays",
                schema: "posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Heatmap",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Reels",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Reels_Category",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "HasVideo",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ReelScore",
                schema: "posts",
                table: "Posts");
        }
    }
}
