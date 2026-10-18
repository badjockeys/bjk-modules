using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.User.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgeCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgeCheckAttempts",
                schema: "users",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgeCheckAttemptsSince",
                schema: "users",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeCheckMethod",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeCheckProvider",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgeCheckRecordedAt",
                schema: "users",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgeCheckResult",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            // Existing accounts haven't been checked yet.
            migrationBuilder.AddColumn<string>(
                name: "AgeCheckStatus",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgeCheckAttempts",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckAttemptsSince",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckMethod",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckProvider",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckRecordedAt",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckResult",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgeCheckStatus",
                schema: "users",
                table: "Accounts");
        }
    }
}
