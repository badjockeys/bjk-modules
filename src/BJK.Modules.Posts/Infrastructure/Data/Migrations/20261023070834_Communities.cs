using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Communities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Communities",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(70)", maxLength: 70, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Rules = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlaceName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Latitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    RadiusKm = table.Column<int>(type: "int", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RemovedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Communities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommunityDiscussions",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsFlagged = table.Column<bool>(type: "bit", nullable: false),
                    ReplyCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityDiscussions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommunityDiscussions_Communities_CommunityId",
                        column: x => x.CommunityId,
                        principalSchema: "posts",
                        principalTable: "Communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityMembers",
                schema: "posts",
                columns: table => new
                {
                    CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityMembers", x => new { x.CommunityId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CommunityMembers_Communities_CommunityId",
                        column: x => x.CommunityId,
                        principalSchema: "posts",
                        principalTable: "Communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityPosts",
                schema: "posts",
                columns: table => new
                {
                    CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SharedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SharedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityPosts", x => new { x.CommunityId, x.PostId });
                    table.ForeignKey(
                        name: "FK_CommunityPosts_Communities_CommunityId",
                        column: x => x.CommunityId,
                        principalSchema: "posts",
                        principalTable: "Communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityReplies",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiscussionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsFlagged = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityReplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommunityReplies_CommunityDiscussions_DiscussionId",
                        column: x => x.DiscussionId,
                        principalSchema: "posts",
                        principalTable: "CommunityDiscussions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Communities_OwnerId",
                schema: "posts",
                table: "Communities",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Communities_RemovedAt_MemberCount",
                schema: "posts",
                table: "Communities",
                columns: new[] { "RemovedAt", "MemberCount" });

            migrationBuilder.CreateIndex(
                name: "IX_Communities_Slug",
                schema: "posts",
                table: "Communities",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommunityDiscussions_AuthorId",
                schema: "posts",
                table: "CommunityDiscussions",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityDiscussions_CommunityId_Status_LastActivityAt",
                schema: "posts",
                table: "CommunityDiscussions",
                columns: new[] { "CommunityId", "Status", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityMembers_CommunityId_Status_JoinedAt",
                schema: "posts",
                table: "CommunityMembers",
                columns: new[] { "CommunityId", "Status", "JoinedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityMembers_UserId_Status",
                schema: "posts",
                table: "CommunityMembers",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityPosts_CommunityId_SharedAt",
                schema: "posts",
                table: "CommunityPosts",
                columns: new[] { "CommunityId", "SharedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityPosts_PostId",
                schema: "posts",
                table: "CommunityPosts",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityReplies_AuthorId",
                schema: "posts",
                table: "CommunityReplies",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityReplies_DiscussionId_Status_CreatedAt",
                schema: "posts",
                table: "CommunityReplies",
                columns: new[] { "DiscussionId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommunityMembers",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "CommunityPosts",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "CommunityReplies",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "CommunityDiscussions",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "Communities",
                schema: "posts");
        }
    }
}
