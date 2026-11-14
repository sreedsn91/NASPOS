using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NasPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyLicenseUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LicenseMaxUsers",
                table: "Companies",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicenseMaxUsers",
                table: "Companies");
        }
    }
}
