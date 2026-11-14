using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NasLicenseManager.Data.Migrations
{
    /// <summary>Keys get any length in months (1-month trial … 20 years) instead of fixed 1/2-year plans.
    /// Existing records convert: 1 year → 12, 2 years → 24, lifetime → NULL.</summary>
    public partial class LengthInMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Months",
                table: "IssuedLicenses",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("UPDATE [IssuedLicenses] SET [Months] = CASE [Plan] WHEN 1 THEN 12 WHEN 2 THEN 24 ELSE NULL END;");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "IssuedLicenses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Plan",
                table: "IssuedLicenses",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)9);

            migrationBuilder.Sql("UPDATE [IssuedLicenses] SET [Plan] = CASE WHEN [Months] IS NULL THEN 9 WHEN [Months] >= 24 THEN 2 ELSE 1 END;");

            migrationBuilder.DropColumn(
                name: "Months",
                table: "IssuedLicenses");
        }
    }
}
