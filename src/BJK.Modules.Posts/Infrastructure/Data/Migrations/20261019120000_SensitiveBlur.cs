using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Sensitive tagging: who set the tag (SensitiveSource; existing tagged posts count as the author's), the automatic
    /// detection score, and moderator review of automatic tags (SensitiveReview, when and by whom), with an index for
    /// the review queue. Only additions; nothing is changed or removed.
    /// </summary>
    public partial class SensitiveBlur : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SensitiveReview",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SensitiveReviewedAt",
                schema: "posts",
                table: "Posts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SensitiveReviewedBy",
                schema: "posts",
                table: "Posts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SensitiveScore",
                schema: "posts",
                table: "Posts",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SensitiveSource",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            // Posts tagged before this change were tagged by their authors or moderators; count them as the author's.
            migrationBuilder.Sql("UPDATE [posts].[Posts] SET [SensitiveSource] = N'Author' WHERE [IsSensitive] = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SensitiveReview_CreatedAt",
                schema: "posts",
                table: "Posts",
                columns: new[] { "SensitiveReview", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_SensitiveReview_CreatedAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SensitiveReview",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SensitiveReviewedAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SensitiveReviewedBy",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SensitiveScore",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SensitiveSource",
                schema: "posts",
                table: "Posts");
        }
    }
}
