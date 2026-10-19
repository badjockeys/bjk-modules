using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <summary>Photos' feed-size copy (feed.webp, 1080 px): its key on media.Media (FeedKey). Only an addition; older photos have none.</summary>
    public partial class FeedImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedKey",
                schema: "media",
                table: "Media",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedKey",
                schema: "media",
                table: "Media");
        }
    }
}
