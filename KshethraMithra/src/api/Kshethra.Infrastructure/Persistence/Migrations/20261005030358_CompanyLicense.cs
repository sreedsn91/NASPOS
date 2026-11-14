using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kshethra.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyLicense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LicenseAppliedAt",
                table: "Companies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LicenseExpiresOn",
                table: "Companies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LicenseGraceUntil",
                table: "Companies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseKey",
                table: "Companies",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LicenseKeyId",
                table: "Companies",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LicenseLastSeenOn",
                table: "Companies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "LicensePlan",
                table: "Companies",
                type: "tinyint",
                nullable: true);

            // Companies that existed before licensing keep billing for 30 days so they can get a key
            // (LicenseService.GraceDaysForExistingCompanies). New companies need a key when they are created.
            migrationBuilder.Sql("UPDATE [Companies] SET [LicenseGraceUntil] = DATEADD(day, 30, CAST(GETDATE() AS date)) WHERE [LicenseKey] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicenseAppliedAt",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicenseExpiresOn",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicenseGraceUntil",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicenseKey",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicenseKeyId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicenseLastSeenOn",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "LicensePlan",
                table: "Companies");
        }
    }
}
