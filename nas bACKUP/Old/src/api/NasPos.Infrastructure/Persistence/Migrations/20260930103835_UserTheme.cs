using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NasPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "Users",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "light");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_Theme",
                table: "Users",
                sql: "[Theme] IN ('light', 'dark', 'system')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_Theme",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "Users");
        }
    }
}
