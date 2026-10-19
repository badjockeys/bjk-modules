using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.User.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Age policy revised (19 Oct 2026): the declared date of birth (13+) and the sign-up liveness result. Adds
    /// columns only. Existing accounts get no date of birth (they're asked at their next sign-in); those whose old
    /// selfie age check got past its liveness step are marked as having passed liveness.
    /// </summary>
    public partial class AddDateOfBirthAndLiveness : Migration
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

            migrationBuilder.AddColumn<int>(
                name: "LivenessAttempts",
                schema: "users",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LivenessAttemptsSince",
                schema: "users",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LivenessCheckedAt",
                schema: "users",
                table: "Accounts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LivenessStatus",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            // The old selfie age check only recorded a face-estimate outcome after its liveness step passed.
            migrationBuilder.Sql(
                "UPDATE [users].[Accounts] SET [LivenessStatus] = N'Passed', [LivenessCheckedAt] = [AgeCheckRecordedAt] "
                + "WHERE [AgeCheckMethod] = N'FaceEstimate';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LivenessAttempts",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LivenessAttemptsSince",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LivenessCheckedAt",
                schema: "users",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LivenessStatus",
                schema: "users",
                table: "Accounts");
        }
    }
}
