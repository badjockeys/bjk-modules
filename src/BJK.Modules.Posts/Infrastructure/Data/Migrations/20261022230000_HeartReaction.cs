using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BJK.Modules.Posts.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Heart replaces Laugh (owner, 22 Oct 2026). Reactions are stored by name, so there's no schema change: existing
    /// Laugh reactions on posts and comments become Like.
    /// </summary>
    public partial class HeartReaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [posts].[Reactions] SET [Type] = N'Like' WHERE [Type] = N'Laugh';");
            migrationBuilder.Sql("UPDATE [posts].[CommentReactions] SET [Type] = N'Like' WHERE [Type] = N'Laugh';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Hearts can't be told apart from Likes that were Laughs, so nothing is undone.
        }
    }
}
