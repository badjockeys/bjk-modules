using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Profiles.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeactivatedAt",
                schema: "profiles",
                table: "Profiles",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeactivatedAt",
                schema: "profiles",
                table: "Profiles");
        }
    }
}
