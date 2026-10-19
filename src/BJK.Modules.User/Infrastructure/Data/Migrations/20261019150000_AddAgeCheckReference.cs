using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.User.Infrastructure.Data.Migrations
{
    /// <summary>
    /// A short support reference (AGE-XXXXXX) for each age check attempt, shown with "Contact support" when the check
    /// fails. Adds a column only.
    /// </summary>
    public partial class AddAgeCheckReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgeCheckReference",
                schema: "users",
                table: "Accounts",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgeCheckReference",
                schema: "users",
                table: "Accounts");
        }
    }
}
