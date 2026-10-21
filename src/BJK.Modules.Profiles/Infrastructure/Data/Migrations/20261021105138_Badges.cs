using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Profiles.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Badges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Badges",
                schema: "profiles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Badge = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AwardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AwardedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Badges", x => new { x.UserId, x.Badge });
                    table.ForeignKey(
                        name: "FK_Badges_Profiles_UserId",
                        column: x => x.UserId,
                        principalSchema: "profiles",
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Badges",
                schema: "profiles");
        }
    }
}
