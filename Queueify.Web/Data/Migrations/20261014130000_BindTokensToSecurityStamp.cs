using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Queueify.Web.Migrations
{
    /// <inheritdoc />
    public partial class BindTokensToSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tokens made before this column existed have no stamp and stop working (the user makes a new one).
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "PersonalAccessTokens",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "PersonalAccessTokens");
        }
    }
}
