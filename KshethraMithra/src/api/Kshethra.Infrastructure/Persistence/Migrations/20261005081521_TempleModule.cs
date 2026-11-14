using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kshethra.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TempleModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeityId",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOffering",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsName",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsStar",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PoojaSlotId",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CalendarCorrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    MonthSign = table.Column<int>(type: "int", nullable: true),
                    MalayalamDay = table.Column<int>(type: "int", nullable: true),
                    Star = table.Column<int>(type: "int", nullable: true),
                    Tithi = table.Column<int>(type: "int", nullable: true),
                    Festival = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarCorrections_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Deities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RegionalName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deities_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Devotees",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    RegionalName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Star = table.Column<int>(type: "int", nullable: true),
                    HouseName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devotees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devotees_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Donations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReceiptNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Date = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DonorName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    HouseName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Star = table.Column<int>(type: "int", nullable: true),
                    DevoteeId = table.Column<long>(type: "bigint", nullable: true),
                    PurposeLedgerId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceivedIntoLedgerId = table.Column<long>(type: "bigint", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Pan = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    VoucherId = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Cancelled = table.Column<bool>(type: "bit", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Donations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Donations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Ledgers_PurposeLedgerId",
                        column: x => x.PurposeLedgerId,
                        principalTable: "Ledgers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Vouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "Vouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PoojaSlots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RegionalName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Time = table.Column<TimeOnly>(type: "time", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoojaSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PoojaSlots_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bookings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BookingNo = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BookedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DevoteeId = table.Column<long>(type: "bigint", nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bookings_Devotees_DevoteeId",
                        column: x => x.DevoteeId,
                        principalTable: "Devotees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bookings_TradeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "TradeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookingLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BookingId = table.Column<long>(type: "bigint", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    DeityId = table.Column<long>(type: "bigint", nullable: true),
                    PoojaSlotId = table.Column<long>(type: "bigint", nullable: true),
                    DevoteeId = table.Column<long>(type: "bigint", nullable: true),
                    DevoteeName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Star = table.Column<int>(type: "int", nullable: true),
                    Qty = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ScheduleKind = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MaxCount = table.Column<int>(type: "int", nullable: true),
                    Weekday = table.Column<int>(type: "int", nullable: true),
                    ScheduleStar = table.Column<int>(type: "int", nullable: true),
                    MalayalamDay = table.Column<int>(type: "int", nullable: true),
                    MonthSign = table.Column<int>(type: "int", nullable: true),
                    Tithi = table.Column<int>(type: "int", nullable: true),
                    Paksha = table.Column<int>(type: "int", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    ScheduleText = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Remarks = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingLines_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingLines_Deities_DeityId",
                        column: x => x.DeityId,
                        principalTable: "Deities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingLines_PoojaSlots_PoojaSlotId",
                        column: x => x.PoojaSlotId,
                        principalTable: "PoojaSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookingOccurrences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    BookingId = table.Column<long>(type: "bigint", nullable: false),
                    BookingLineId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DoneBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingOccurrences_BookingLines_BookingLineId",
                        column: x => x.BookingLineId,
                        principalTable: "BookingLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingOccurrences_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingOccurrences_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingLines_BookingId",
                table: "BookingLines",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingLines_DeityId",
                table: "BookingLines",
                column: "DeityId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingLines_PoojaSlotId",
                table: "BookingLines",
                column: "PoojaSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingLines_ProductId",
                table: "BookingLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingOccurrences_BookingId",
                table: "BookingOccurrences",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingOccurrences_BookingLineId",
                table: "BookingOccurrences",
                column: "BookingLineId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingOccurrences_CompanyId_Date_Status",
                table: "BookingOccurrences",
                columns: new[] { "CompanyId", "Date", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CompanyId_BookedAt",
                table: "Bookings",
                columns: new[] { "CompanyId", "BookedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CompanyId_BookingNo",
                table: "Bookings",
                columns: new[] { "CompanyId", "BookingNo" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_DevoteeId",
                table: "Bookings",
                column: "DevoteeId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_DocumentId",
                table: "Bookings",
                column: "DocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarCorrections_CompanyId_Date",
                table: "CalendarCorrections",
                columns: new[] { "CompanyId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deities_CompanyId_Name",
                table: "Deities",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devotees_CompanyId_Name",
                table: "Devotees",
                columns: new[] { "CompanyId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Devotees_CompanyId_Phone",
                table: "Devotees",
                columns: new[] { "CompanyId", "Phone" });

            migrationBuilder.CreateIndex(
                name: "IX_Donations_CompanyId_Date",
                table: "Donations",
                columns: new[] { "CompanyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Donations_PurposeLedgerId",
                table: "Donations",
                column: "PurposeLedgerId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_VoucherId",
                table: "Donations",
                column: "VoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_PoojaSlots_CompanyId_Name",
                table: "PoojaSlots",
                columns: new[] { "CompanyId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingOccurrences");

            migrationBuilder.DropTable(
                name: "CalendarCorrections");

            migrationBuilder.DropTable(
                name: "Donations");

            migrationBuilder.DropTable(
                name: "BookingLines");

            migrationBuilder.DropTable(
                name: "Bookings");

            migrationBuilder.DropTable(
                name: "Deities");

            migrationBuilder.DropTable(
                name: "PoojaSlots");

            migrationBuilder.DropTable(
                name: "Devotees");

            migrationBuilder.DropColumn(
                name: "DeityId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsOffering",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NeedsName",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NeedsStar",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PoojaSlotId",
                table: "Products");
        }
    }
}
