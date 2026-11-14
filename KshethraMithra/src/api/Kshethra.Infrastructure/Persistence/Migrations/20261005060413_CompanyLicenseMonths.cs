using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kshethra.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// License length becomes any number of months (1 month trial … 20 years) instead of fixed 1/2-year plans.
    /// Existing keys keep working: 1 year → 12 months, 2 years → 24, lifetime → NULL.
    /// </summary>
    public partial class CompanyLicenseMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LicenseMonths",
                table: "Companies",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE [Companies] SET [LicenseMonths] = CASE [LicensePlan] WHEN 1 THEN 12 WHEN 2 THEN 24 ELSE NULL END;");

            migrationBuilder.DropColumn(
                name: "LicensePlan",
                table: "Companies");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "LicensePlan",
                table: "Companies",
                type: "tinyint",
                nullable: true);

            migrationBuilder.Sql("UPDATE [Companies] SET [LicensePlan] = CASE WHEN [LicenseKey] IS NULL THEN NULL WHEN [LicenseMonths] IS NULL THEN 9 WHEN [LicenseMonths] >= 24 THEN 2 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "LicenseMonths",
                table: "Companies");
        }
    }
}
