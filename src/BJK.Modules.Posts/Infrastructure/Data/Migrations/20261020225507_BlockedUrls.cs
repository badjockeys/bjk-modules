using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlockedUrls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedUrls",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedUrls", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedUrls_Source_Pattern",
                schema: "posts",
                table: "BlockedUrls",
                columns: new[] { "Source", "Pattern" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedUrls",
                schema: "posts");
        }
    }
}
