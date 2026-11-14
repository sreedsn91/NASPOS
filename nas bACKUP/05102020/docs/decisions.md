# Decisions

Choices made where the specification or legacy behaviour was silent or had to change.

1. **SQL Server instead of PostgreSQL** — chosen by the product owner. Money uses `decimal(18,2)` for posted amounts
   and `decimal(18,4)` for rates/quantities; timestamps are `datetimeoffset`.
2. **.NET 9** (the installed SDK) rather than .NET 8. Upgrading to .NET 10 LTS is a retarget + package bump.
3. **One `TradeDocument` table** for all inventory vouchers (sales, POS, returns, orders, notes, purchases, opening
   stock, adjustment, production) with `VoucherType` as discriminator, instead of four legacy header tables with
   near-identical columns. Accounting vouchers stay separate (`Voucher`/`VoucherLine`).
4. **Account legs as single-sided rows** (`LedgerId`, `ContraLedgerId`, `Debit`, `Credit`) written in balanced pairs,
   rather than the legacy two-ledger row. Journals with several Dr/Cr lines post one leg per line.
5. **Every stock line has a batch.** When batch tracking is off, a default batch (product barcode or code) is used,
   so stock valuation and multi-MRP work uniformly.
6. **Landed cost** spreads freight/other expenses by taxable value and divides by paid + free quantity (free goods
   lower the unit cost); tax is added to cost when input tax is not claimable.
7. **Opening stock in the balance sheet** is shown as "Opening stock (capital b/f)" on the liabilities side so the
   sheet balances without a stock ledger; closing stock is valued at batch cost.
8. **Round-off "Up"** leaves exact rupee amounts unchanged (the legacy code added ₹0.50 to them).
9. **Loyalty** points are proportional (`bill ÷ sale amount × points`, 2 dp); redemption is a discount posted to
   *Discount Allowed*; balances carry forward at year-end.
10. **Kerala flood cess** applies only to intra-state B2C sales inside the configured date window and is not claimed
    on purchases.
11. **B2CL threshold** is ₹1,00,000 (revised August 2024).
12. **Negative stock / credit limit "Warn"** returns HTTP 409 with the warnings; the client asks for confirmation
    and resubmits with `acknowledgeWarnings`. "Block" rejects; batch rows are locked (`UPDLOCK`) during the check.
13. **Posted documents are cancelled, not deleted.** Only unposted orders/estimates can be deleted.
14. **Roles are shared across companies** of one installation (a single organisation); memberships assign a role
    per company.
15. **Access tokens live in memory**; a page reload silently refreshes from the HttpOnly cookie.
16. **Licensing omitted** (spec §14 allows omission for this deployment).
17. **Sales screens default to tax-inclusive unit prices** (retail MRP-style pricing); configurable per voucher type
    in Screen Options.
