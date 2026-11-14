using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Reports;
using NasPos.Domain;

namespace NasPos.Application.Admin;

public sealed record MonthPoint(string Month, decimal Sales, decimal Purchase, decimal Orders);
public sealed record CounterPoint(string Counter, int Bills, decimal Cash, decimal Card, decimal Credit, decimal Total);
public sealed record TopItem(string Name, decimal Qty, decimal Amount);
public sealed record Reminder(string Kind, string Text, string? Link);
public sealed record OpenOrder(long Id, string DocumentNo, string VoucherType, DateTimeOffset Date, string? Party, decimal Amount, DateOnly? DeliveryDate);

public sealed record DashboardDto(
    decimal TodaySales, int TodayBills, decimal MonthSales, decimal YearSales, decimal YearPurchase, decimal CashBalance, decimal BankBalance,
    decimal Receivables, decimal Payables, int LowStockItems, int ExpiringBatches, List<MonthPoint> Months, List<CounterPoint> Counters,
    List<TopItem> TopItems, List<OpenOrder> OpenOrders, List<Reminder> Reminders);

public class DashboardService(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var fy = await db.FinancialYears.AsNoTracking().FirstAsync(f => f.Id == user.FinancialYearId, ct);
        var today = clock.Today();
        var dayStart = clock.DayStart(today);
        var dayEnd = clock.DayEnd(today);
        var monthStart = clock.DayStart(new DateOnly(today.Year, today.Month, 1));
        var sales = ReportData.SalesTypes;
        var purchases = ReportData.PurchaseTypes;
        var docs = db.TradeDocuments.AsNoTracking().Where(d => d.FinancialYearId == fy.Id && !d.Cancelled);

        var todayDocs = await docs.Where(d => sales.Contains(d.VoucherType) && d.Date >= dayStart && d.Date < dayEnd)
            .Select(d => new { d.Counter, d.BillAmount, d.CashAmount, d.CardAmount, d.CreditAmount }).ToListAsync(ct);
        decimal monthSales = await docs.Where(d => sales.Contains(d.VoucherType) && d.Date >= monthStart).SumAsync(d => (decimal?)d.BillAmount, ct) ?? 0;

        var monthly = await docs.Where(d => sales.Contains(d.VoucherType) || purchases.Contains(d.VoucherType) || d.VoucherType == VoucherType.SalesOrder)
            .Select(d => new { d.Date, d.VoucherType, d.BillAmount }).ToListAsync(ct);
        var months = new List<MonthPoint>();
        for (var m = new DateOnly(fy.StartDate.Year, fy.StartDate.Month, 1); m <= fy.EndDate; m = m.AddMonths(1))
        {
            var inMonth = monthly.Where(x => { var d = clock.BusinessDate(x.Date); return d.Year == m.Year && d.Month == m.Month; }).ToList();
            months.Add(new MonthPoint(m.ToString("MMM yy"), inMonth.Where(x => sales.Contains(x.VoucherType)).Sum(x => x.BillAmount),
                inMonth.Where(x => purchases.Contains(x.VoucherType)).Sum(x => x.BillAmount),
                inMonth.Where(x => x.VoucherType == VoucherType.SalesOrder).Sum(x => x.BillAmount)));
        }

        var cashBank = await db.Ledgers.AsNoTracking().Where(l => l.AccountGroup!.SystemCode == SystemGroup.CashInHand || l.AccountGroup.SystemCode == SystemGroup.BankAccounts)
            .Select(l => new { l.Id, Cash = l.AccountGroup!.SystemCode == SystemGroup.CashInHand }).ToListAsync(ct);
        var cbIds = cashBank.Select(c => c.Id).ToList();
        var cbBal = await db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy.Id && cbIds.Contains(e.LedgerId))
            .GroupBy(e => e.LedgerId).Select(g => new { g.Key, Bal = g.Sum(e => e.Debit - e.Credit) }).ToListAsync(ct);
        decimal cash = cbBal.Where(b => cashBank.First(c => c.Id == b.Key).Cash).Sum(b => b.Bal);
        decimal bank = cbBal.Where(b => !cashBank.First(c => c.Id == b.Key).Cash).Sum(b => b.Bal);

        var partyBal = await db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy.Id && (e.Ledger!.PartyType == PartyType.Customer || e.Ledger.PartyType == PartyType.Supplier))
            .GroupBy(e => new { e.LedgerId, e.Ledger!.PartyType }).Select(g => new { g.Key.PartyType, Bal = g.Sum(e => e.Debit - e.Credit) }).ToListAsync(ct);
        decimal receivables = partyBal.Where(p => p.Bal > 0).Sum(p => p.Bal);
        decimal payables = -partyBal.Where(p => p.Bal < 0).Sum(p => p.Bal);

        var stockByProduct = await db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy.Id).GroupBy(m => m.ProductId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) }).ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
        var rol = await db.Products.AsNoTracking().Where(p => p.IsActive && p.ReorderLevel > 0).Select(p => new { p.Id, p.ReorderLevel }).ToListAsync(ct);
        int low = rol.Count(p => stockByProduct.GetValueOrDefault(p.Id) <= p.ReorderLevel);
        var expiryLimit = today.AddDays(30);
        var expiring = await db.Batches.AsNoTracking().Where(b => b.IsActive && b.ExpiryDate != null && b.ExpiryDate <= expiryLimit).Select(b => b.Id).ToListAsync(ct);
        var expiringQty = await db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy.Id && expiring.Contains(m.BatchId)).GroupBy(m => m.BatchId)
            .Select(g => g.Sum(m => m.QtyIn - m.QtyOut)).CountAsync(q => q > 0, ct);

        var monthDocIds = docs.Where(d => sales.Contains(d.VoucherType) && d.Date >= monthStart).Select(d => d.Id);
        var top = await db.TradeDocumentLines.AsNoTracking().Where(l => monthDocIds.Contains(l.DocumentId))
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.Qty * x.UnitFactor), Amount = g.Sum(x => x.LineTotal) })
            .OrderByDescending(x => x.Amount).Take(8)
            .Join(db.Products, x => x.ProductId, p => p.Id, (x, p) => new TopItem(p.Name, x.Qty, x.Amount))
            .ToListAsync(ct);

        var openOrders = await docs.Where(d => (d.VoucherType == VoucherType.SalesOrder || d.VoucherType == VoucherType.PurchaseOrder || d.VoucherType == VoucherType.Estimate) && !d.IsProcessed)
            .OrderBy(d => d.DeliveryDate ?? DateOnly.MaxValue).ThenBy(d => d.Date).Take(10)
            .Select(d => new OpenOrder(d.Id, d.DocumentNo, d.VoucherType.ToString(), d.Date, d.PartyName, d.BillAmount, d.DeliveryDate)).ToListAsync(ct);

        var reminders = new List<Reminder>();
        var chequeLimit = today.AddDays(3);
        var cheques = await db.Vouchers.AsNoTracking().Where(v => !v.Cancelled && v.ChequeStatus == ChequeStatus.Pending && v.ChequeDate != null && v.ChequeDate <= chequeLimit)
            .OrderBy(v => v.ChequeDate).Take(10).Select(v => new { v.Id, v.ChequeNo, v.ChequeDate, v.Total, v.VoucherType }).ToListAsync(ct);
        reminders.AddRange(cheques.Select(c => new Reminder("Cheque", $"{c.VoucherType} cheque {c.ChequeNo} for {c.Total:0.00} due {c.ChequeDate:dd-MM}", $"voucher:{c.Id}")));
        var overdue = await db.BillOutstandings.AsNoTracking().Where(b => !b.Cancelled && b.Side == EntrySide.Dr && b.Settled < b.Amount && b.DueDate != null && b.DueDate < today)
            .CountAsync(ct);
        if (overdue > 0) reminders.Add(new Reminder("Overdue", $"{overdue} customer bills are past their due date", "report:outstanding-bills"));
        var now = clock.Now;
        var offersEnding = await db.ItemOffers.AsNoTracking().CountAsync(o => o.IsActive && o.EndAt >= now && o.EndAt <= now.AddDays(2), ct);
        if (offersEnding > 0) reminders.Add(new Reminder("Offer", $"{offersEnding} item offers end within 2 days", "offers"));
        if (low > 0) reminders.Add(new Reminder("Stock", $"{low} items are at or below reorder level", "report:stock-reorder"));
        if (expiringQty > 0) reminders.Add(new Reminder("Expiry", $"{expiringQty} batches expire within 30 days", "report:stock-expiry"));
        var queuedFailed = await db.OutboundMessages.AsNoTracking().CountAsync(m => m.Status == MessageStatus.Failed, ct);
        if (queuedFailed > 0) reminders.Add(new Reminder("Messages", $"{queuedFailed} SMS/e-mails failed to send", "messaging"));

        return new DashboardDto(
            todayDocs.Sum(d => d.BillAmount), todayDocs.Count, monthSales, months.Sum(m => m.Sales), months.Sum(m => m.Purchase), cash, bank,
            receivables, payables, low, expiringQty, months,
            todayDocs.GroupBy(d => d.Counter ?? "-").Select(g => new CounterPoint(g.Key, g.Count(), g.Sum(d => d.CashAmount), g.Sum(d => d.CardAmount), g.Sum(d => d.CreditAmount), g.Sum(d => d.BillAmount))).OrderBy(c => c.Counter).ToList(),
            top, openOrders, reminders);
    }
}
