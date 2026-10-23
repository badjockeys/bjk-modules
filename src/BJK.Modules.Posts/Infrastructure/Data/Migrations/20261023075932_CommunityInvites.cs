using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommunityInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InvitedAt",
                schema: "posts",
                table: "CommunityMembers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvitedBy",
                schema: "posts",
                table: "CommunityMembers",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvitedAt",
                schema: "posts",
                table: "CommunityMembers");

            migrationBuilder.DropColumn(
                name: "InvitedBy",
                schema: "posts",
                table: "CommunityMembers");
        }
    }
}
