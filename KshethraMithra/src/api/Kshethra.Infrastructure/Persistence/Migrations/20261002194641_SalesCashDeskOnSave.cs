using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kshethra.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sales Invoice and Local Sales now ask for the cash tendered and show the balance, like Touch POS:
    /// switch "Cash desk on save" on for those two voucher types (1 = Sales, 2 = LocalSales) in existing companies.
    /// Seeded options are PascalCase JSON, options saved from the screen are camelCase; both are matched case-sensitively.
    /// </summary>
    public partial class SalesCashDeskOnSave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE InvoiceSettings
                SET OptionsJson = REPLACE(REPLACE(OptionsJson COLLATE Latin1_General_CS_AS,
                        '"CashDeskOnSave":false', '"CashDeskOnSave":true'),
                        '"cashDeskOnSave":false', '"cashDeskOnSave":true')
                WHERE VoucherType IN (1, 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous per-company choice is not recorded, so there is nothing to restore; users can untick the option.
        }
    }
}
