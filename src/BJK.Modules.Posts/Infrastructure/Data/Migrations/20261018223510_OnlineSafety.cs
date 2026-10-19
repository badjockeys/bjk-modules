using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OnlineSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reports_ReporterId_TargetType_TargetId",
                schema: "posts",
                table: "Reports");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReporterId",
                schema: "posts",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "PostId",
                schema: "posts",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcknowledgedAt",
                schema: "posts",
                table: "Reports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "posts",
                table: "Reports",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAt",
                schema: "posts",
                table: "Reports",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutcomeNotifiedAt",
                schema: "posts",
                table: "Reports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                schema: "posts",
                table: "Reports",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReporterName",
                schema: "posts",
                table: "Reports",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "posts",
                table: "Reports",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetUrl",
                schema: "posts",
                table: "Reports",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlagged",
                schema: "posts",
                table: "Posts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                schema: "posts",
                table: "Posts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "posts",
                table: "Posts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                schema: "posts",
                table: "Posts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreeningLabels",
                schema: "posts",
                table: "Posts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScreeningScore",
                schema: "posts",
                table: "Posts",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFlagged",
                schema: "posts",
                table: "Comments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentCommentId",
                schema: "posts",
                table: "Comments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "posts",
                table: "Comments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                schema: "posts",
                table: "Comments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreeningLabels",
                schema: "posts",
                table: "Comments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScreeningScore",
                schema: "posts",
                table: "Comments",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Appeals",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppellantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PostId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Statement = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appeals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appeals_Posts_PostId",
                        column: x => x.PostId,
                        principalSchema: "posts",
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommentReactions",
                schema: "posts",
                columns: table => new
                {
                    CommentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentReactions", x => new { x.CommentId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CommentReactions_Comments_CommentId",
                        column: x => x.CommentId,
                        principalSchema: "posts",
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModerationAuditLog",
                schema: "posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorRole = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Module = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    After = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationAuditLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId_TargetType_TargetId",
                schema: "posts",
                table: "Reports",
                columns: new[] { "ReporterId", "TargetType", "TargetId" },
                unique: true,
                filter: "[ReporterId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status_Priority_DueAt",
                schema: "posts",
                table: "Reports",
                columns: new[] { "Status", "Priority", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_IsFlagged_Status_CreatedAt",
                schema: "posts",
                table: "Posts",
                columns: new[] { "IsFlagged", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_IsFlagged_Status_CreatedAt",
                schema: "posts",
                table: "Comments",
                columns: new[] { "IsFlagged", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_ParentCommentId_Status_CreatedAt",
                schema: "posts",
                table: "Comments",
                columns: new[] { "ParentCommentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Appeals_AppellantId_TargetType_TargetId",
                schema: "posts",
                table: "Appeals",
                columns: new[] { "AppellantId", "TargetType", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appeals_PostId",
                schema: "posts",
                table: "Appeals",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_Appeals_Status_DueAt",
                schema: "posts",
                table: "Appeals",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CommentReactions_UserId",
                schema: "posts",
                table: "CommentReactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAuditLog_Action_OccurredAt",
                schema: "posts",
                table: "ModerationAuditLog",
                columns: new[] { "Action", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAuditLog_ActorId_OccurredAt",
                schema: "posts",
                table: "ModerationAuditLog",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAuditLog_OccurredAt",
                schema: "posts",
                table: "ModerationAuditLog",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAuditLog_TargetType_TargetId",
                schema: "posts",
                table: "ModerationAuditLog",
                columns: new[] { "TargetType", "TargetId" });

            // Reports made before this migration came from the app, at normal priority, due 48 hours after they were made.
            migrationBuilder.Sql(
                "UPDATE [posts].[Reports] SET [Source] = N'App', [Priority] = N'Normal', [DueAt] = DATEADD(hour, 48, [CreatedAt]) "
                + "WHERE [Source] = N'';");

            // Round existing map pins to about 500 m, exactly as PinRounding does for new ones: latitude to a 0.0045
            // degree grid, then longitude to a 0.0045 / cos(latitude) degree grid. Rows are updated, never deleted.
            migrationBuilder.Sql(
                "UPDATE [posts].[Posts] SET [Latitude] = "
                + "CASE WHEN ROUND(ROUND([Latitude] / 0.0045, 0) * 0.0045, 6) > 90 THEN 90 "
                + "WHEN ROUND(ROUND([Latitude] / 0.0045, 0) * 0.0045, 6) < -90 THEN -90 "
                + "ELSE ROUND(ROUND([Latitude] / 0.0045, 0) * 0.0045, 6) END;");
            migrationBuilder.Sql(
                "UPDATE p SET [Longitude] = CASE WHEN r.[Lng] > 180 THEN 180 WHEN r.[Lng] < -180 THEN -180 ELSE r.[Lng] END "
                + "FROM [posts].[Posts] p "
                + "CROSS APPLY (SELECT 0.0045E0 / CASE WHEN COS(RADIANS(CAST(p.[Latitude] AS float))) < 0.01E0 THEN 0.01E0 "
                + "ELSE COS(RADIANS(CAST(p.[Latitude] AS float))) END AS [Step]) s "
                + "CROSS APPLY (SELECT CAST(ROUND(ROUND(CAST(p.[Longitude] AS float) / s.[Step], 0) * s.[Step], 6) AS decimal(9, 6)) AS [Lng]) r;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Appeals",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "CommentReactions",
                schema: "posts");

            migrationBuilder.DropTable(
                name: "ModerationAuditLog",
                schema: "posts");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReporterId_TargetType_TargetId",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_Status_Priority_DueAt",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Posts_IsFlagged_Status_CreatedAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Comments_IsFlagged_Status_CreatedAt",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropIndex(
                name: "IX_Comments_ParentCommentId_Status_CreatedAt",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "DueAt",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "OutcomeNotifiedAt",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "ReporterName",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "TargetUrl",
                schema: "posts",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "IsFlagged",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "IsSensitive",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ScreeningLabels",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ScreeningScore",
                schema: "posts",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "IsFlagged",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ParentCommentId",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ScreeningLabels",
                schema: "posts",
                table: "Comments");

            migrationBuilder.DropColumn(
                name: "ScreeningScore",
                schema: "posts",
                table: "Comments");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReporterId",
                schema: "posts",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PostId",
                schema: "posts",
                table: "Reports",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId_TargetType_TargetId",
                schema: "posts",
                table: "Reports",
                columns: new[] { "ReporterId", "TargetType", "TargetId" },
                unique: true);
        }
    }
}
