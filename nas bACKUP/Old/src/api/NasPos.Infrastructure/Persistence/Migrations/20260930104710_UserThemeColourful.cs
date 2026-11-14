using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NasPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserThemeColourful : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Theme",
                table: "Users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Theme",
                table: "Users",
                sql: "[Theme] IN ('light', 'dark', 'system', 'ocean', 'sunset')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Theme",
                table: "Users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Theme",
                table: "Users",
                sql: "[Theme] IN ('light', 'dark', 'system')");
        }
    }
}
