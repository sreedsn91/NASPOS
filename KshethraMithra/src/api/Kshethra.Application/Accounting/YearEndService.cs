using System.Data;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Application.Reports;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Posting;

namespace Kshethra.Application.Accounting;

public sealed record YearEndPreview(string CurrentYear, DateOnly NextStart, DateOnly NextEnd, bool NextYearExists, decimal TrialBalanceDifference,
    int LedgersToCarry, decimal NetProfit, int BatchesToCarry, decimal ClosingStockValue, int NegativeBatches, int LoyaltyCustomers,
    IReadOnlyList<string> Blockers);

public sealed record YearEndResult(long NewFinancialYearId, string NewYearName, int OpeningVouchers, long? OpeningStockDocumentId, int LoyaltyRows);

/// <summary>
/// Closes the active financial year in ONE transaction on the same database: creates the next year, carries ledger balances
/// (P&amp;L result into the Profit &amp; Loss ledger), item/batch closing stock and loyalty points forward, and marks the year
/// read-only. Nothing is truncated; the closed year stays fully queryable.
/// </summary>
public class YearEndService(IAppDbContext db, ICurrentUser user, IClock clock, SystemLedgerResolver systemLedgers, NumberingService numbering,
    AccountReports accountReports, AuditService audit)
{
    private const string CarryNarration = "Carried forward from";

    public async Task<YearEndPreview> PreviewAsync(CancellationToken ct)
    {
        var fy = await db.FinancialYears.AsNoTracking().FirstAsync(f => f.Id == user.FinancialYearId, ct);
        var blockers = new List<string>();
        if (fy.IsClosed) blockers.Add("This financial year is already closed.");
        var next = await db.FinancialYears.AsNoTracking().FirstOrDefaultAsync(f => f.StartDate == fy.EndDate.AddDays(1), ct);
        if (next is not null && await db.Vouchers.AnyAsync(v => v.FinancialYearId == next.Id && v.VoucherType == VoucherType.Opening && v.Narration!.StartsWith(CarryNarration), ct))
            blockers.Add("Balances were already carried into the next year.");
        if (await db.FinancialYears.AnyAsync(f => f.StartDate < fy.StartDate && !f.IsClosed, ct))
            blockers.Add("An earlier financial year is still open; close it first.");

        var bals = await LedgerBalancesAsync(fy.Id, ct);
        decimal diff = bals.Values.Sum();
        if (diff != 0) blockers.Add($"The trial balance is out by {diff:0.00}.");
        var stock = await BatchBalancesAsync(fy.Id, ct);
        var pnl = await accountReports.ComputePnLAsync(null, fy.EndDate, ct);
        int loyalty = await db.LoyaltyTransactions.Where(t => t.FinancialYearId == fy.Id).GroupBy(t => t.LedgerId)
            .CountAsync(g => g.Sum(t => t.PointsIn - t.PointsOut) > 0, ct);
        return new YearEndPreview(fy.Name, fy.EndDate.AddDays(1), fy.EndDate.AddYears(1), next is not null, diff,
            bals.Count(b => b.Value != 0), pnl.NetProfit, stock.Count(s => s.Qty != 0), pnl.ClosingStock, stock.Count(s => s.Qty < 0), loyalty, blockers);
    }

    private async Task<Dictionary<long, decimal>> LedgerBalancesAsync(long fyId, CancellationToken ct) =>
        await db.AccountEntries.Where(e => e.FinancialYearId == fyId).GroupBy(e => e.LedgerId)
            .Select(g => new { g.Key, Bal = g.Sum(e => e.Debit - e.Credit) }).ToDictionaryAsync(x => x.Key, x => x.Bal, ct);

    private sealed record BatchBal(long BatchId, long ProductId, decimal Qty, decimal Cost, decimal Mrp, decimal SaleRate, string Code);

    private async Task<List<BatchBal>> BatchBalancesAsync(long fyId, CancellationToken ct)
    {
        var sums = await db.StockMovements.Where(m => m.FinancialYearId == fyId).GroupBy(m => m.BatchId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) }).Where(x => x.Qty != 0).ToListAsync(ct);
        var ids = sums.Select(s => s.Key).ToList();
        var batches = await db.Batches.AsNoTracking().Include(b => b.Product).Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        return sums.Select(s =>
        {
            var b = batches[s.Key];
            return new BatchBal(b.Id, b.ProductId, s.Qty, b.Cost > 0 ? b.Cost : b.Product!.PurchaseRate, b.Mrp, b.SaleRate, b.Code);
        }).ToList();
    }

    public async Task<YearEndResult> CloseAsync(CancellationToken ct)
    {
        var preview = await PreviewAsync(ct);
        if (preview.Blockers.Count > 0) throw new DomainException(string.Join(" ", preview.Blockers), "year_end_blocked");

        await using var tx = await db.BeginAsync(IsolationLevel.Serializable, ct);
        var fy = await db.FinancialYears.FirstAsync(f => f.Id == user.FinancialYearId, ct);
        var next = await db.FinancialYears.FirstOrDefaultAsync(f => f.StartDate == fy.EndDate.AddDays(1), ct);
        if (next is null)
        {
            next = new FinancialYear
            {
                StartDate = fy.EndDate.AddDays(1), EndDate = fy.EndDate.AddYears(1), PreviousYearId = fy.Id,
                Name = $"{fy.EndDate.AddDays(1).Year}-{(fy.EndDate.AddDays(1).Year + 1) % 100:00}",
            };
            db.FinancialYears.Add(next);
            await db.SaveChangesAsync(ct);
            var prefixes = await db.DocumentSequences.Where(s => s.FinancialYearId == fy.Id).ToListAsync(ct);
            foreach (var p in prefixes) db.DocumentSequences.Add(new DocumentSequence { FinancialYearId = next.Id, VoucherType = p.VoucherType, Prefix = p.Prefix });
            await db.SaveChangesAsync(ct);
        }
        var openingDate = clock.DayStart(next.StartDate);
        var L = await systemLedgers.GetAsync(ct);
        long pnlLedger = L(SystemLedger.ProfitLoss), obLedger = L(SystemLedger.OpeningBalance);
        var ctx = new PostingContext { Ledger = L };

        // 1. Ledger balances: assets/liabilities carry as-is; income/expense roll into the Profit & Loss ledger.
        var bals = await LedgerBalancesAsync(fy.Id, ct);
        var natures = await db.Ledgers.AsNoTracking().Select(l => new { l.Id, l.AccountGroup!.Nature }).ToDictionaryAsync(x => x.Id, x => x.Nature, ct);
        var carry = new Dictionary<long, decimal>();
        foreach (var (ledgerId, bal) in bals.Where(b => b.Value != 0))
        {
            long target = natures[ledgerId] is AccountNature.Income or AccountNature.Expense ? pnlLedger : ledgerId;
            carry[target] = carry.GetValueOrDefault(target) + bal;
        }
        int vouchers = 0;
        foreach (var (ledgerId, bal) in carry.Where(c => c.Value != 0 && c.Key != obLedger))
        {
            var (seq, no) = await numbering.NextAsync(VoucherType.Opening, next.Id, ct);
            var v = new Voucher
            {
                FinancialYearId = next.Id, VoucherType = VoucherType.Opening, DocumentNo = no, SequenceNo = seq, Date = openingDate,
                Total = Math.Abs(bal), Narration = $"{CarryNarration} {fy.Name}", UserId = user.UserId, Counter = user.Machine,
                Lines = [new VoucherLine { LineNo = 1, LedgerId = ledgerId, Side = bal > 0 ? EntrySide.Dr : EntrySide.Cr, Amount = Math.Abs(bal) }],
            };
            db.Vouchers.Add(v);
            await db.SaveChangesAsync(ct);
            foreach (var leg in PostingEngine.PostVoucher(v, ctx).Legs)
                db.AccountEntries.Add(new AccountEntry
                {
                    FinancialYearId = next.Id, VoucherType = VoucherType.Opening, VoucherId = v.Id, DocumentNo = v.DocumentNo, Date = openingDate,
                    LedgerId = leg.LedgerId, ContraLedgerId = leg.ContraLedgerId, Debit = leg.Debit, Credit = leg.Credit, Remarks = leg.Remarks,
                });
            vouchers++;
        }

        // 2. Closing stock per batch becomes opening stock (negatives carried as an adjustment out).
        var stock = await BatchBalancesAsync(fy.Id, ct);
        long? openingDocId = null;
        foreach (var (type, rows) in new[] { (VoucherType.OpeningStock, stock.Where(s => s.Qty > 0).ToList()), (VoucherType.StockAdjustment, stock.Where(s => s.Qty < 0).ToList()) })
        {
            if (rows.Count == 0) continue;
            var (seq, no) = await numbering.NextAsync(type, next.Id, ct);
            var doc = new TradeDocument
            {
                FinancialYearId = next.Id, VoucherType = type, DocumentNo = no, SequenceNo = seq, Date = openingDate, TaxType = TaxType.NoTax,
                Remarks = $"{CarryNarration} {fy.Name}", UserId = user.UserId, Counter = user.Machine,
            };
            int n = 1;
            foreach (var s in rows)
            {
                decimal qty = Math.Abs(s.Qty);
                doc.Lines.Add(new TradeDocumentLine
                {
                    LineNo = n++, ProductId = s.ProductId, BatchId = s.BatchId, BatchCode = s.Code, Qty = qty, UnitFactor = 1, Rate = s.Cost,
                    Mrp = s.Mrp, SaleRate = s.SaleRate, Cost = s.Cost, Gross = Math.Round(qty * s.Cost, 2), Taxable = Math.Round(qty * s.Cost, 2),
                    LineTotal = Math.Round(qty * s.Cost, 2), Direction = s.Qty > 0 ? StockDirection.In : StockDirection.Out,
                });
            }
            doc.GrossAmount = doc.TaxableAmount = doc.ExemptAmount = 0;
            doc.GrossAmount = doc.Lines.Sum(l => l.Gross);
            doc.ExemptAmount = doc.GrossAmount;
            doc.BillAmount = doc.GrossAmount;
            doc.Cost = doc.GrossAmount;
            db.TradeDocuments.Add(doc);
            await db.SaveChangesAsync(ct);
            foreach (var leg in PostingEngine.PostTrade(doc, ctx).Stock)
                db.StockMovements.Add(new StockMovement
                {
                    FinancialYearId = next.Id, VoucherType = type, TradeDocumentId = doc.Id, DocumentNo = doc.DocumentNo, Date = openingDate,
                    ProductId = leg.ProductId, BatchId = leg.BatchId, QtyIn = leg.QtyIn, QtyOut = leg.QtyOut, Mrp = leg.Mrp, SaleRate = leg.SaleRate, Cost = leg.Cost,
                });
            if (type == VoucherType.OpeningStock) openingDocId = doc.Id;
        }

        // 3. Loyalty point balances.
        var points = await db.LoyaltyTransactions.Where(t => t.FinancialYearId == fy.Id).GroupBy(t => new { t.LedgerId, t.SchemeId })
            .Select(g => new { g.Key.LedgerId, g.Key.SchemeId, Bal = g.Sum(t => t.PointsIn - t.PointsOut) }).Where(x => x.Bal > 0).ToListAsync(ct);
        foreach (var p in points)
            db.LoyaltyTransactions.Add(new LoyaltyTransaction
            {
                FinancialYearId = next.Id, LedgerId = p.LedgerId, SchemeId = p.SchemeId, VoucherType = VoucherType.Opening, Date = openingDate,
                DocumentNo = $"{CarryNarration} {fy.Name}", PointsIn = p.Bal,
            });

        // 4. Lock the closed year.
        fy.IsClosed = true;
        fy.ClosedAt = clock.Now;
        fy.ClosedBy = user.UserId;
        audit.Log(Screens.YearEnd, "Close", $"Closed {fy.Name}; opened {next.Name} with {vouchers} balances, {stock.Count} batches, {points.Count} loyalty balances");
        await db.SaveChangesAsync(ct);

        // Final guard: the new year's opening books must balance.
        var newDiff = await db.AccountEntries.Where(e => e.FinancialYearId == next.Id).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0;
        if (newDiff != 0) throw new DomainException($"Carried-forward balances do not balance ({newDiff:0.00}); nothing was changed.", "unbalanced");
        await tx.CommitAsync(ct);
        return new YearEndResult(next.Id, next.Name, vouchers, openingDocId, points.Count);
    }

    /// <summary>Creates the next year without closing (so billing can start on 1 April while the old year is finalised).</summary>
    public async Task<long> CreateNextYearAsync(CancellationToken ct)
    {
        var latest = await db.FinancialYears.OrderByDescending(f => f.StartDate).FirstAsync(ct);
        var start = latest.EndDate.AddDays(1);
        var next = new FinancialYear { StartDate = start, EndDate = start.AddYears(1).AddDays(-1), PreviousYearId = latest.Id, Name = $"{start.Year}-{(start.Year + 1) % 100:00}" };
        db.FinancialYears.Add(next);
        await db.SaveChangesAsync(ct);
        foreach (var p in await db.DocumentSequences.Where(s => s.FinancialYearId == latest.Id).ToListAsync(ct))
            db.DocumentSequences.Add(new DocumentSequence { FinancialYearId = next.Id, VoucherType = p.VoucherType, Prefix = p.Prefix });
        audit.Log(Screens.YearEnd, "Add", $"Created financial year {next.Name}");
        await db.SaveChangesAsync(ct);
        return next.Id;
    }
}
