using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Engagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoteClosesAt",
                schema: "posts",
                table: "Posts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoteResultSentAt",
                schema: "posts",
                table: "Posts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuthorPreferences",
                schema: "posts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoteDuration = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorPreferences", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Bookmarks",
                schema: "posts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookmarks", x => new { x.UserId, x.PostId });
                    table.ForeignKey(
                        name: "FK_Bookmarks_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostFollows",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostFollows", x => new { x.PostId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PostFollows_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostShares",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    SharedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostShares", x => new { x.PostId, x.ViewerKey, x.Day });
                    table.ForeignKey(
                        name: "FK_PostShares_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostViews",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewerKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsLocal = table.Column<bool>(type: "bit", nullable: false),
                    ViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostViews", x => new { x.PostId, x.ViewerKey });
                    table.ForeignKey(
                        name: "FK_PostViews_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostVotes",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Choice = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostVotes", x => new { x.PostId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PostVotes_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_VoteResultPending",
                schema: "posts",
                table: "Posts",
                column: "VoteClosesAt",
                filter: "[VoteClosesAt] IS NOT NULL AND [VoteResultSentAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_PostId",
                schema: "posts",
                table: "Bookmarks",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_UserId_CreatedAt",
                schema: "posts",
                table: "Bookmarks",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostFollows_UserId",
                schema: "posts",
                table: "PostFollows",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PostShares_ViewerKey",
                schema: "posts",
                table: "PostShares",
                column: "ViewerKey");

            migrationBuilder.CreateIndex(
                name: "IX_PostViews_ViewerKey",
                schema: "posts",
                table: "PostViews",
                column: "ViewerKey");

            migrationBuilder.CreateIndex(
                name: "IX_PostVotes_UserId",
                schema: "posts",
                table: "PostVotes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorPreferences",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "Bookmarks",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "PostFollows",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "PostShares",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "PostViews",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "PostVotes",
                schema: "posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_VoteResultPending",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VoteClosesAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "VoteResultSentAt",
                schema: "posts",
                table: "Posts");
        }
    }
}
