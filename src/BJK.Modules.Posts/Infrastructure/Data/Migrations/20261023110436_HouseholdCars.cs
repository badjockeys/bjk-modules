using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HouseholdCars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilmedFrom",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FilmedFromCarId",
                schema: "posts",
                table: "Posts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HouseholdCars",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Make = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Colour = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Nickname = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdCars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_FilmedFromCarId",
                schema: "posts",
                table: "Posts",
                column: "FilmedFromCarId",
                filter: "[FilmedFromCarId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdCars_UserId_CreatedAt",
                schema: "posts",
                table: "HouseholdCars",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HouseholdCars",
                schema: "posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_FilmedFromCarId",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FilmedFrom",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "FilmedFromCarId",
                schema: "posts",
                table: "Posts");
        }
    }
}
