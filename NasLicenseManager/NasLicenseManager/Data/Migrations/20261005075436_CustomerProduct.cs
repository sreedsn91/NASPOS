using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NasLicenseManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Product",
                table: "IssuedLicenses",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NASPOS");

            migrationBuilder.AddColumn<string>(
                name: "Product",
                table: "Customers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NASPOS");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Product",
                table: "IssuedLicenses");

            migrationBuilder.DropColumn(
                name: "Product",
                table: "Customers");
        }
    }
}
