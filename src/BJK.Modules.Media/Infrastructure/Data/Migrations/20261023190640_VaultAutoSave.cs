using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class VaultAutoSave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoSaveOff",
                schema: "media",
                table: "VaultAccounts",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSaveOff",
                schema: "media",
                table: "VaultAccounts");
        }
    }
}
