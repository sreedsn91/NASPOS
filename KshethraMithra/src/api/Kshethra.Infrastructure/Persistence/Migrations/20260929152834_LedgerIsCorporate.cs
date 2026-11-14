using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kshethra.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LedgerIsCorporate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ledgers_CompanyId_Name",
                table: "Ledgers");

            migrationBuilder.AddColumn<bool>(
                name: "IsCorporate",
                table: "Ledgers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_CompanyId_Name_Individual",
                table: "Ledgers",
                columns: new[] { "CompanyId", "Name" },
                unique: true,
                filter: "[IsCorporate] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_CompanyId_Name_IsCorporate",
                table: "Ledgers",
                columns: new[] { "CompanyId", "Name", "IsCorporate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ledgers_CompanyId_Name_Individual",
                table: "Ledgers");

            migrationBuilder.DropIndex(
                name: "IX_Ledgers_CompanyId_Name_IsCorporate",
                table: "Ledgers");

            migrationBuilder.DropColumn(
                name: "IsCorporate",
                table: "Ledgers");

            migrationBuilder.CreateIndex(
                name: "IX_Ledgers_CompanyId_Name",
                table: "Ledgers",
                columns: new[] { "CompanyId", "Name" },
                unique: true);
        }
    }
}
