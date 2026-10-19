using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.User.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Age policy revised (19 Oct 2026): the declared date of birth (13+). Adds a column only. Existing accounts get
    /// none; they're asked for it at their next sign-in.
    /// </summary>
    public partial class AddDateOfBirth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "users",
                table: "Accounts",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "users",
                table: "Accounts");
        }
    }
}
