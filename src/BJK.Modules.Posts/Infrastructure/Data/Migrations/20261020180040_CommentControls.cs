using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommentControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommentSetting",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Everyone");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAt",
                schema: "posts",
                table: "Comments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HiddenByPostAuthorAt",
                schema: "posts",
                table: "Comments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHiddenByPostAuthor",
                schema: "posts",
                table: "Comments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CommentEdits",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentEdits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommentEdits_Comments_CommentId",
                        column: x => x.CommentId,
                        principalSchema: "posts",
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommentEdits_CommentId_ReplacedAt",
                schema: "posts",
                table: "CommentEdits",
                columns: new[] { "CommentId", "ReplacedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommentEdits",
                schema: "posts");

            migrationBuilder.DropColumn(
                name: "CommentSetting",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "HiddenByPostAuthorAt",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "IsHiddenByPostAuthor",
                schema: "posts",
                table: "Comments");
        }
    }
}
