using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Media.Infrastructure.Data.Migrations
{
    /// <summary>Automatic blurring: the NeedsReview status (stored as text, so no change), review details, blur counts and manual blur regions.</summary>
    public partial class MediaRedaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FacesBlurred",
                schema: "media",
                table: "Media",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualRedactionsJson",
                schema: "media",
                table: "Media",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlatesBlurred",
                schema: "media",
                table: "Media",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                schema: "media",
                table: "Media",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "media",
                table: "Media",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                schema: "media",
                table: "Media",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                schema: "media",
                table: "Media",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FacesBlurred",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ManualRedactionsJson",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "PlatesBlurred",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "media",
                table: "Media");

            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "media",
                table: "Media");
        }
    }
}
