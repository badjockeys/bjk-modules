using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FastPosting : Migration
    {
        /// <summary>
        /// Post now, publish when ready: each post's media state (existing posts are all <c>Ready</c>: their media had
        /// to be ready before they could be posted), why it couldn't be published, when its media became ready, and the
        /// author's address for the "couldn't be published" email while it's pending. Only new columns.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaFailureReason",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MediaReadyAt",
                schema: "posts",
                table: "Posts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaState",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Ready");

            migrationBuilder.AddColumn<string>(
                name: "NotifyEmail",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaFailureReason",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "MediaReadyAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "MediaState",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NotifyEmail",
                schema: "posts",
                table: "Posts");
        }
    }
}
