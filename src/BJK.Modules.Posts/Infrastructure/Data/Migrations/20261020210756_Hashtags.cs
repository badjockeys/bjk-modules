using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hashtags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Hashtags",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Display = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FirstUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TotalUses = table.Column<int>(type: "int", nullable: false),
                    IsHiddenFromTrends = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hashtags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HashtagHourlyCounts",
                schema: "posts",
                columns: table => new
                {
                    HashtagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hour = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Posts = table.Column<int>(type: "int", nullable: false),
                    Authors = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HashtagHourlyCounts", x => new { x.HashtagId, x.Hour });
                    table.ForeignKey(
                        name: "FK_HashtagHourlyCounts_Hashtags_HashtagId",
                        column: x => x.HashtagId,
                        principalSchema: "posts",
                        principalTable: "Hashtags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostHashtags",
                schema: "posts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HashtagId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostHashtags", x => new { x.PostId, x.HashtagId });
                    table.ForeignKey(
                        name: "FK_PostHashtags_Hashtags_HashtagId",
                        column: x => x.HashtagId,
                        principalSchema: "posts",
                        principalTable: "Hashtags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PostHashtags_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HashtagHourlyCounts_Hour",
                schema: "posts",
                table: "HashtagHourlyCounts",
                column: "Hour")
                .Annotation("SqlServer:Include", new[] { "HashtagId", "Posts" });

            migrationBuilder.CreateIndex(
                name: "IX_Hashtags_Key",
                schema: "posts",
                table: "Hashtags",
                column: "Key",
                unique: true)
                .Annotation("SqlServer:Include", new[] { "Display", "TotalUses", "IsHiddenFromTrends" });

            migrationBuilder.CreateIndex(
                name: "IX_PostHashtags_HashtagId_PostedAt",
                schema: "posts",
                table: "PostHashtags",
                columns: new[] { "HashtagId", "PostedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostHashtags_PostedAt",
                schema: "posts",
                table: "PostHashtags",
                column: "PostedAt")
                .Annotation("SqlServer:Include", new[] { "HashtagId", "AuthorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HashtagHourlyCounts",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "PostHashtags",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "Hashtags",
                schema: "posts");
        }
    }
}
