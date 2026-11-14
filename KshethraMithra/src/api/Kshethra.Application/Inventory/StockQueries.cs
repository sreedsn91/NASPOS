using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;

namespace Kshethra.Application.Inventory;

public sealed record BatchStock(long BatchId, long ProductId, decimal Qty);

public class StockQueries(IAppDbContext db, ICurrentUser user)
{
    /// <summary>On-hand quantity (base units) per batch within a financial year (opening stock is carried into each year).</summary>
    public async Task<Dictionary<long, decimal>> OnHandByBatchAsync(IEnumerable<long> batchIds, long? financialYearId = null, CancellationToken ct = default)
    {
        long fy = financialYearId ?? user.FinancialYearId;
        var ids = batchIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.StockMovements.Where(m => m.FinancialYearId == fy && ids.Contains(m.BatchId))
            .GroupBy(m => m.BatchId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
    }

    public async Task<Dictionary<long, decimal>> OnHandByProductAsync(IEnumerable<long>? productIds = null, long? financialYearId = null, CancellationToken ct = default)
    {
        long fy = financialYearId ?? user.FinancialYearId;
        var q = db.StockMovements.Where(m => m.FinancialYearId == fy);
        if (productIds is not null)
        {
            var ids = productIds.Distinct().ToList();
            q = q.Where(m => ids.Contains(m.ProductId));
        }
        return await q.GroupBy(m => m.ProductId)
            .Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
    }
}

public class LedgerQueries(IAppDbContext db, ICurrentUser user)
{
    /// <summary>Closing balance (Dr positive) of ledgers within the active financial year.</summary>
    public async Task<Dictionary<long, decimal>> BalancesAsync(IEnumerable<long> ledgerIds, long? financialYearId = null, DateTimeOffset? upTo = null, CancellationToken ct = default)
    {
        long fy = financialYearId ?? user.FinancialYearId;
        var ids = ledgerIds.Distinct().ToList();
        var q = db.AccountEntries.Where(e => e.FinancialYearId == fy && ids.Contains(e.LedgerId));
        if (upTo is not null) q = q.Where(e => e.Date <= upTo);
        return await q.GroupBy(e => e.LedgerId).Select(g => new { g.Key, Bal = g.Sum(e => e.Debit - e.Credit) })
            .ToDictionaryAsync(x => x.Key, x => x.Bal, ct);
    }

    public async Task<decimal> BalanceAsync(long ledgerId, long? financialYearId = null, CancellationToken ct = default) =>
        (await BalancesAsync([ledgerId], financialYearId, null, ct)).GetValueOrDefault(ledgerId);

    public async Task<decimal> LoyaltyBalanceAsync(long ledgerId, long? financialYearId = null, CancellationToken ct = default)
    {
        long fy = financialYearId ?? user.FinancialYearId;
        return await db.LoyaltyTransactions.Where(t => t.LedgerId == ledgerId && t.FinancialYearId == fy)
            .SumAsync(t => (decimal?)(t.PointsIn - t.PointsOut), ct) ?? 0;
    }
}
