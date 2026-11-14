# Architecture

```
Angular SPA ──HTTPS / JWT──> ASP.NET Core API ──EF Core (parameters only)──> SQL Server
     └──── SignalR /hubs/live <────┘            └── OutboxDispatcher (SMS / e-mail)
```

## Layers

- **Domain** (`NasPos.Domain`) — entities and enums; `DocumentCalculator` (GST line/bill maths, round-off, landed cost,
  loyalty points) and `PostingEngine` (document → balanced Dr/Cr legs + stock movements). Both are pure functions with
  exhaustive unit tests; nothing in the domain touches the database or HTTP.
- **Application** — use-case services. `DocumentService` saves every inventory document in one transaction:
  header, lines, batches, account legs, stock movements, bill outstanding, loyalty, rate updates, audit row.
  `VoucherService` does the same for receipts/payments/journals. Reports are `IReportProvider`s returning a generic
  `ReportResult` that one grid, one Excel exporter and one PDF renderer handle.
- **Infrastructure** — `AppDbContext` (global tenant filter on `CompanyId`, audit stamping, cross-company write guard),
  entity configurations (97 foreign keys, 25 check constraints, unique indexes), migrations, logical backup/restore,
  SMS/e-mail providers and the outbox dispatcher.
- **Api** — thin controllers; `[Permission(screen, right)]` enforces role rights on the server for every endpoint;
  `[RequireStepUp]` demands a fresh password confirmation for destructive actions; ProblemDetails error mapping;
  rate limiting; SignalR hub.

## Key design points

- **One database, tenant by company.** Every business row carries `CompanyId`; EF applies a query filter and
  `SaveChanges` rejects cross-company writes. Year-end closing is an ordinary transaction inside the same database.
- **Financial years.** Documents belong to a year; balances and stock are summed per year. Closing carries balances,
  batch stock and loyalty forward as opening entries and marks the year read-only. Nothing is deleted.
- **Numbering.** `DocumentSequence` rows are incremented with `UPDATE … OUTPUT` under a row lock inside the save
  transaction — concurrent counters always get distinct numbers (covered by an integration test).
- **Edits** reverse the document's postings and repost under the same id and number, bumping `Version`;
  optimistic concurrency uses SQL Server `rowversion`. A SignalR soft lock warns when two users open one document.
- **Server-authoritative maths.** The Angular client mirrors the calculator for instant totals; the server
  recalculates from product-master tax rates on save.
- **Postings reference real documents.** `AccountEntry` has nullable FKs to `TradeDocument` *or* `Voucher`
  (check constraint: exactly one). Stock movements, bills and loyalty rows reference the real document id.

## Legacy module → new implementation

| Legacy (Xenia POS Pro R2) | New |
|---|---|
| frmSalePOS | `/pos` `PosComponent` (tiles, keypad, scale barcodes, pole display via Web Serial, cash desk) |
| frmInvoice (Sales, Local Sales, Estimate, Order, Return, Delivery Note) | `/doc/:type` `DocumentEditorComponent` + `DocumentService` |
| frmPurchase (Purchase, Local, PO, Return, Receipt Note), frmOpening | same editor in purchase mode (landed cost, batches, expiry, rate updates) |
| frmStockAdjustment, frmProduction, frmBOM | editor in stock-journal mode (In/Out lines, BOM load, cost spread) + `/masters/boms` |
| InsertData / DeleteData / CancelData | `PostingEngine` + `DocumentService.SaveAsync/CancelAsync/DeleteAsync` (transactional) |
| frmReceipt (Receipt, Payment, Journal, Contra, DN, CN, Opening) | `/voucher/:type` + `VoucherService` (bill-wise allocation), `/accounts/opening` |
| accDayBook, accAnalysis, accReceiptNPayment | Day book, trial balance, P&L, balance sheet, R&P reports |
| frmProduct, frmCustomer, vendor, frmLedger, frmAccountGroup, frmEmployee | `/masters/*` screens + services with delete guards |
| Area/category/manufacturer/price list/SMS event lists | `/masters/lookups/:kind` (one shared screen) |
| frmLoyaltyManager, frmItemwiseOffer, Pricelist + updater | `/masters/loyalty`, `/masters/offers`, `/masters/price-lists` |
| frmSaleReport, Purchase/Stock/Account/Analysis/Counter/Tax reports, rptGstr1 | `/reports` catalogue (~70 reports), GSTR-1 workbook |
| frmSettings, frmCompanyProfile, InvoiceSettings, PrintSettings | `/settings/features`, `/settings/company`, `/settings/screens`, `/settings/printing` |
| frmUser, frmRolePermission, frmUserlog | `/settings/users`, `/settings/roles` (matrix), `/settings/audit` |
| frmCreateCompany, frmBackupDB/RestoreDB | `/settings/companies` (logical JSON backup, restore into new company) |
| frmAccountClosing | `/settings/year-end` + `YearEndService` |
| frmProductImport, frmledgerImport, frmBoardUpdator, frmExportTransactions | `/tools/import-export`, PLU export/import, `/tools/transfer` |
| frmBCManager, frmPrintBarcode, frmlabelPrint | `/inventory/labels` (Code 128 SVG, label templates) |
| frmSendSMS, invoice mail | `MessagingService` templates + outbox, `/settings/messaging` |
| SQL runner, "repair", "clear vouchers" | **removed**; replaced by operator migrations and a guarded, audited, multi-step reset |
| Licence / trial / hardware ID | **removed** (not needed for this deployment) |
