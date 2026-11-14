using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using NasPos.Domain.Entities;
using static NasPos.Application.Reports.RowExt;

namespace NasPos.Application.Reports;

public class AccountReports(ReportData data) : IReportProvider
{
    public IEnumerable<ReportDefinition> Definitions =>
    [
        new("day-book", "Day Book", "Accounts", Screens.AccountReports, [F.Dates, F.VoucherType, F.Ledger], "All postings in date order"),
        new("ledger-statement", "Ledger Statement", "Accounts", Screens.AccountReports, [F.Dates, F.Ledger], "Running balance of one ledger"),
        new("cash-book", "Cash Book", "Accounts", Screens.AccountReports, [F.Dates]),
        new("bank-book", "Bank Book", "Accounts", Screens.AccountReports, [F.Dates, F.Ledger]),
        new("group-summary", "Group Summary", "Accounts", Screens.AccountReports, [F.AsOn, F.Group]),
        new("account-list", "Account List", "Accounts", Screens.AccountReports, [F.Group]),
        new("trial-balance", "Trial Balance", "Final Accounts", Screens.AccountReports, [F.AsOn, F.Detailed]),
        new("profit-loss", "Profit & Loss Account", "Final Accounts", Screens.AccountReports, [F.Dates]),
        new("balance-sheet", "Balance Sheet", "Final Accounts", Screens.AccountReports, [F.AsOn]),
        new("receipts-payments", "Receipts & Payments Account", "Final Accounts", Screens.AccountReports, [F.Dates]),
        new("ageing", "Outstanding Ageing (Bill-by-Bill)", "Outstanding", Screens.AccountReports, [F.Side, F.Party, F.Area]),
        new("outstanding-bills", "Outstanding Bills", "Outstanding", Screens.AccountReports, [F.Side, F.Party]),
        new("credit-summary", "Credit Summary (Balance vs Limit)", "Outstanding", Screens.AccountReports, [F.Side, F.Area]),
        new("cheque-register", "Cheque Register", "Accounts", Screens.AccountReports, [F.Dates, F.Ledger]),
    ];

    public async Task<ReportResult> RunAsync(string key, ReportFilter f, CancellationToken ct)
    {
        var r = key switch
        {
            "day-book" => await DayBook(f, ct),
            "ledger-statement" => await Statement(f, f.LedgerId ?? f.PartyId ?? throw new DomainException("Select a ledger."), ct),
            "cash-book" => await Statement(f, await SystemLedgerId(SystemLedger.Cash, ct), ct),
            "bank-book" => await Statement(f, f.LedgerId ?? await SystemLedgerId(SystemLedger.Bank, ct), ct),
            "group-summary" => await GroupSummary(f, ct),
            "account-list" => await AccountList(f, ct),
            "trial-balance" => await TrialBalance(f, ct),
            "profit-loss" => await ProfitLoss(f, ct),
            "balance-sheet" => await BalanceSheet(f, ct),
            "receipts-payments" => await ReceiptsPayments(f, ct),
            "ageing" => await Ageing(f, ct),
            "outstanding-bills" => await OutstandingBills(f, ct),
            "credit-summary" => await CreditSummary(f, ct),
            "cheque-register" => await ChequeRegister(f, ct),
            _ => throw new NotFoundException($"Unknown report {key}."),
        };
        r.Key = key;
        return r;
    }

    private Task<long> SystemLedgerId(SystemLedger code, CancellationToken ct) =>
        data.Db.Ledgers.Where(l => l.SystemCode == code).Select(l => l.Id).FirstAsync(ct);

    private async Task<ReportResult> DayBook(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var q = data.Db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy && e.Date >= start && e.Date < end);
        if (f.VoucherType is not null) q = q.Where(e => e.VoucherType == f.VoucherType);
        if (f.LedgerId is not null) q = q.Where(e => e.LedgerId == f.LedgerId);
        var rows = await q.OrderBy(e => e.Date).ThenBy(e => e.TradeDocumentId ?? e.VoucherId).ThenBy(e => e.Id)
            .Select(e => new { e.Date, e.VoucherType, e.DocumentNo, e.TradeDocumentId, e.VoucherId, Ledger = e.Ledger!.Name, Contra = e.ContraLedger != null ? e.ContraLedger.Name : null, e.Debit, e.Credit, e.Remarks })
            .ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Day Book", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("type", "Type"), new("no", "Voucher No"), new("ledger", "Ledger"), new("contra", "Against"),
                new("remarks", "Particulars"), new("dr", "Debit", ColType.Money, true), new("cr", "Credit", ColType.Money, true)],
        };
        foreach (var e in rows)
            r.Rows.Add(Row(("date", data.D(e.Date)), ("type", e.VoucherType.DisplayName()), ("no", e.DocumentNo), ("ledger", e.Ledger), ("contra", e.Contra),
                ("remarks", e.Remarks), ("dr", e.Debit), ("cr", e.Credit)).Link(e.TradeDocumentId is not null ? $"document:{e.TradeDocumentId}" : $"voucher:{e.VoucherId}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Statement(ReportFilter f, long ledgerId, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var ledger = await data.Db.Ledgers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ledgerId, ct) ?? throw new NotFoundException("Ledger not found.");
        var all = data.Db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy && e.LedgerId == ledgerId);
        decimal opening = await all.Where(e => e.Date < start).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0;
        var rows = await all.Where(e => e.Date >= start && e.Date < end).OrderBy(e => e.Date).ThenBy(e => e.Id)
            .Select(e => new { e.Date, e.VoucherType, e.DocumentNo, e.TradeDocumentId, e.VoucherId, Contra = e.ContraLedger != null ? e.ContraLedger.Name : null, e.Debit, e.Credit, e.Remarks })
            .ToListAsync(ct);
        var r = new ReportResult
        {
            Title = $"Ledger Statement — {ledger.Name}", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("type", "Type"), new("no", "Voucher No"), new("contra", "Particulars"), new("remarks", "Narration"),
                new("dr", "Debit", ColType.Money, true), new("cr", "Credit", ColType.Money, true), new("balance", "Balance")],
        };
        r.Rows.Add(Row(("date", from.ToString("yyyy-MM-dd")), ("contra", "Opening balance"), ("balance", DrCr(opening))).Style("subtotal"));
        decimal bal = opening;
        // Collapse the legs of one document into one line per contra ledger for readability.
        foreach (var g in rows.GroupBy(e => (e.TradeDocumentId, e.VoucherId, e.DocumentNo, e.VoucherType, e.Date)))
        {
            decimal dr = g.Sum(e => e.Debit), cr = g.Sum(e => e.Credit);
            bal += dr - cr;
            string contra = string.Join(", ", g.Select(e => e.Contra).Where(c => c != null).Distinct());
            r.Rows.Add(Row(("date", data.D(g.Key.Date)), ("type", g.Key.VoucherType.DisplayName()), ("no", g.Key.DocumentNo), ("contra", contra),
                ("remarks", string.Join("; ", g.Select(e => e.Remarks).Where(x => x != null).Distinct().Take(3))), ("dr", dr), ("cr", cr), ("balance", DrCr(bal)))
                .Link(g.Key.TradeDocumentId is not null ? $"document:{g.Key.TradeDocumentId}" : $"voucher:{g.Key.VoucherId}"));
        }
        r.ComputeTotals();
        r.Summary.Add(new("Opening", DrCr(opening)));
        r.Summary.Add(new("Closing", DrCr(bal)));
        return r;
    }

    public static string DrCr(decimal v) => v == 0 ? "0.00" : $"{Math.Abs(v):0.00} {(v > 0 ? "Dr" : "Cr")}";

    private sealed record LedgerBal(long Id, string Code, string Name, long GroupId, decimal Opening, decimal Debit, decimal Credit)
    {
        public decimal Closing => Opening + Debit - Credit;
    }

    /// <summary>Per-ledger opening (entries before "from"), period Dr/Cr, within the active year.</summary>
    private async Task<List<LedgerBal>> BalancesAsync(DateOnly? from, DateOnly to, CancellationToken ct)
    {
        long fy = data.FinancialYearId;
        var end = data.Clock.DayEnd(to);
        var start = from is null ? DateTimeOffset.MinValue : data.Clock.DayStart(from.Value);
        var sums = await data.Db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy && e.Date < end)
            .GroupBy(e => e.LedgerId)
            .Select(g => new
            {
                g.Key,
                Open = g.Where(e => e.Date < start).Sum(e => e.Debit - e.Credit),
                Dr = g.Where(e => e.Date >= start).Sum(e => e.Debit),
                Cr = g.Where(e => e.Date >= start).Sum(e => e.Credit),
            }).ToDictionaryAsync(x => x.Key, ct);
        var ledgers = await data.Db.Ledgers.AsNoTracking().Select(l => new { l.Id, l.Code, l.Name, l.AccountGroupId }).ToListAsync(ct);
        return ledgers.Select(l => sums.TryGetValue(l.Id, out var s)
            ? new LedgerBal(l.Id, l.Code, l.Name, l.AccountGroupId, s.Open, s.Dr, s.Cr)
            : new LedgerBal(l.Id, l.Code, l.Name, l.AccountGroupId, 0, 0, 0)).ToList();
    }

    private async Task<Dictionary<long, AccountGroup>> GroupsAsync(CancellationToken ct) =>
        await data.Db.AccountGroups.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);

    private static AccountGroup Root(AccountGroup g, Dictionary<long, AccountGroup> all)
    {
        var c = g;
        while (c.ParentId is not null && all.TryGetValue(c.ParentId.Value, out var p)) c = p;
        return c;
    }

    private static bool IsUnder(AccountGroup g, long ancestorId, Dictionary<long, AccountGroup> all)
    {
        for (var c = g; c is not null; c = c.ParentId is null ? null : all.GetValueOrDefault(c.ParentId.Value))
            if (c.Id == ancestorId) return true;
        return false;
    }

    private async Task<ReportResult> TrialBalance(ReportFilter f, CancellationToken ct)
    {
        var (_, to) = await data.RangeAsync(f, ct);
        var bals = (await BalancesAsync(null, to, ct)).Where(b => b.Debit != 0 || b.Credit != 0 || b.Opening != 0).ToList();
        var groups = await GroupsAsync(ct);
        var r = new ReportResult
        {
            Title = "Trial Balance", Subtitle = $"As on {to:dd-MM-yyyy}",
            Columns = [new("name", "Particulars"), new("dr", "Debit", ColType.Money, true), new("cr", "Credit", ColType.Money, true)],
        };
        foreach (var g in bals.GroupBy(b => groups[b.GroupId]).OrderBy(g => g.Key.Nature).ThenBy(g => g.Key.Name))
        {
            decimal net = g.Sum(b => b.Closing);
            if (f.Detailed)
            {
                r.Rows.Add(Row(("name", g.Key.Name)).Style("header"));
                foreach (var b in g.OrderBy(b => b.Name).Where(b => b.Closing != 0))
                    r.Rows.Add(Row(("name", "    " + b.Name), ("dr", b.Closing > 0 ? b.Closing : 0m), ("cr", b.Closing < 0 ? -b.Closing : 0m)).Link($"ledger:{b.Id}"));
                r.Rows.Add(Row(("name", $"Total {g.Key.Name}"), ("dr", net > 0 ? net : 0m), ("cr", net < 0 ? -net : 0m)).Style("subtotal"));
            }
            else
                foreach (var b in g.OrderBy(b => b.Name).Where(b => b.Closing != 0))
                    r.Rows.Add(Row(("name", b.Name), ("dr", b.Closing > 0 ? b.Closing : 0m), ("cr", b.Closing < 0 ? -b.Closing : 0m)).Link($"ledger:{b.Id}"));
        }
        r.ComputeTotals();
        decimal diff = bals.Sum(b => b.Closing);
        r.Summary.Add(new("Difference", diff.ToString("0.00")));
        r.Summary.Add(new("Status", diff == 0 ? "Books balance" : "OUT OF BALANCE"));
        return r;
    }

    /// <summary>Opening stock = value of opening-stock documents of the year; closing stock = on-hand at batch cost.</summary>
    public async Task<(decimal Opening, decimal Closing)> StockValuesAsync(DateOnly to, CancellationToken ct)
    {
        long fy = data.FinancialYearId;
        var end = data.Clock.DayEnd(to);
        decimal opening = await data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.VoucherType == VoucherType.OpeningStock)
            .SumAsync(m => (decimal?)(m.QtyIn * m.Cost), ct) ?? 0;
        var perBatch = await data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.Date < end)
            .GroupBy(m => m.BatchId).Select(g => new { g.Key, Qty = g.Sum(m => m.QtyIn - m.QtyOut) }).ToListAsync(ct);
        var ids = perBatch.Select(b => b.Key).ToList();
        var costs = await data.Db.Batches.AsNoTracking().Where(b => ids.Contains(b.Id))
            .Select(b => new { b.Id, Cost = b.Cost > 0 ? b.Cost : b.Product!.PurchaseRate }).ToDictionaryAsync(b => b.Id, b => b.Cost, ct);
        decimal closing = perBatch.Where(b => b.Qty > 0).Sum(b => b.Qty * costs.GetValueOrDefault(b.Key));
        return (Math.Round(opening, 2), Math.Round(closing, 2));
    }

    public sealed record PnL(decimal Sales, decimal DirectIncome, decimal OpeningStock, decimal ClosingStock, decimal Purchases,
        decimal DirectExpense, decimal GrossProfit, decimal IndirectIncome, decimal IndirectExpense, decimal NetProfit,
        List<(string Name, long Id, decimal Amount, string Section)> Lines);

    public async Task<PnL> ComputePnLAsync(DateOnly? from, DateOnly to, CancellationToken ct)
    {
        var bals = await BalancesAsync(from, to, ct);
        var groups = await GroupsAsync(ct);
        var stock = await StockValuesAsync(to, ct);
        var salesGroup = groups.Values.FirstOrDefault(g => g.SystemCode == SystemGroup.SalesAccounts);
        var purchaseGroup = groups.Values.FirstOrDefault(g => g.SystemCode == SystemGroup.PurchaseAccounts);
        var lines = new List<(string, long, decimal, string)>();
        decimal sales = 0, dInc = 0, purch = 0, dExp = 0, iInc = 0, iExp = 0;
        foreach (var b in bals)
        {
            var g = groups[b.GroupId];
            // P&L uses the movement within the period (from year start when no "from" is given).
            decimal amt = from is null ? b.Closing : b.Debit - b.Credit;
            if (amt == 0) continue;
            if (g.Nature == AccountNature.Income)
            {
                decimal v = -amt;
                if (salesGroup is not null && IsUnder(g, salesGroup.Id, groups)) { sales += v; lines.Add((b.Name, b.Id, v, "Sales")); }
                else if (g.IsDirect || Root(g, groups).IsDirect) { dInc += v; lines.Add((b.Name, b.Id, v, "DirectIncome")); }
                else { iInc += v; lines.Add((b.Name, b.Id, v, "IndirectIncome")); }
            }
            else if (g.Nature == AccountNature.Expense)
            {
                if (purchaseGroup is not null && IsUnder(g, purchaseGroup.Id, groups)) { purch += amt; lines.Add((b.Name, b.Id, amt, "Purchases")); }
                else if (g.IsDirect || Root(g, groups).IsDirect) { dExp += amt; lines.Add((b.Name, b.Id, amt, "DirectExpense")); }
                else { iExp += amt; lines.Add((b.Name, b.Id, amt, "IndirectExpense")); }
            }
        }
        decimal gross = sales + dInc + stock.Closing - stock.Opening - purch - dExp;
        decimal net = gross + iInc - iExp;
        return new PnL(sales, dInc, stock.Opening, stock.Closing, purch, dExp, gross, iInc, iExp, net, lines);
    }

    private async Task<ReportResult> ProfitLoss(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var fy = await data.Db.FinancialYears.AsNoTracking().FirstAsync(x => x.Id == data.FinancialYearId, ct);
        var p = await ComputePnLAsync(from <= fy.StartDate ? null : from, to, ct);
        var r = new ReportResult
        {
            Title = "Profit & Loss Account", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            Columns = [new("name", "Particulars"), new("amount", "Amount", ColType.Money), new("total", "Total", ColType.Money)],
        };
        void Section(string title, string section, decimal total, bool less = false)
        {
            r.Rows.Add(Row(("name", (less ? "Less: " : "") + title), ("total", less ? -total : total)).Style("header"));
            foreach (var l in p.Lines.Where(l => l.Section == section).OrderByDescending(l => Math.Abs(l.Amount)))
                r.Rows.Add(Row(("name", "    " + l.Name), ("amount", l.Amount)).Link($"ledger:{l.Id}"));
        }
        r.Rows.Add(Row(("name", "TRADING ACCOUNT")).Style("title"));
        Section("Sales (net of returns)", "Sales", p.Sales);
        if (p.DirectIncome != 0) Section("Direct income", "DirectIncome", p.DirectIncome);
        r.Rows.Add(Row(("name", "Closing stock"), ("total", p.ClosingStock)).Style("header"));
        r.Rows.Add(Row(("name", "Less: Opening stock"), ("total", -p.OpeningStock)).Style("header"));
        Section("Purchases (net of returns)", "Purchases", p.Purchases, true);
        if (p.DirectExpense != 0) Section("Direct expenses", "DirectExpense", p.DirectExpense, true);
        r.Rows.Add(Row(("name", p.GrossProfit >= 0 ? "GROSS PROFIT" : "GROSS LOSS"), ("total", p.GrossProfit)).Style("total"));
        r.Rows.Add(Row(("name", "PROFIT & LOSS ACCOUNT")).Style("title"));
        r.Rows.Add(Row(("name", "Gross profit b/f"), ("total", p.GrossProfit)).Style("header"));
        Section("Indirect income", "IndirectIncome", p.IndirectIncome);
        Section("Indirect expenses", "IndirectExpense", p.IndirectExpense, true);
        r.Rows.Add(Row(("name", p.NetProfit >= 0 ? "NET PROFIT" : "NET LOSS"), ("total", p.NetProfit)).Style("total"));
        r.Summary.Add(new("Gross profit", p.GrossProfit.ToString("0.00")));
        r.Summary.Add(new("Net profit", p.NetProfit.ToString("0.00")));
        return r;
    }

    private async Task<ReportResult> BalanceSheet(ReportFilter f, CancellationToken ct)
    {
        var (_, to) = await data.RangeAsync(f, ct);
        var bals = await BalancesAsync(null, to, ct);
        var groups = await GroupsAsync(ct);
        var pnl = await ComputePnLAsync(null, to, ct);
        var r = new ReportResult
        {
            Title = "Balance Sheet", Subtitle = $"As on {to:dd-MM-yyyy}",
            Columns = [new("name", "Particulars"), new("amount", "Amount", ColType.Money), new("total", "Total", ColType.Money)],
        };

        decimal Block(AccountNature nature, string title)
        {
            r.Rows.Add(Row(("name", title)).Style("title"));
            decimal grand = 0;
            var roots = bals.Where(b => groups[b.GroupId].Nature == nature && b.Closing != 0).GroupBy(b => Root(groups[b.GroupId], groups)).OrderBy(g => g.Key.Name);
            foreach (var g in roots)
            {
                // Liabilities are shown as positive credit balances; assets as positive debit balances.
                decimal sign = nature == AccountNature.Asset ? 1 : -1;
                decimal total = g.Sum(b => b.Closing) * sign;
                grand += total;
                r.Rows.Add(Row(("name", g.Key.Name), ("total", total)).Style("header"));
                foreach (var b in g.OrderBy(b => b.Name))
                    r.Rows.Add(Row(("name", "    " + b.Name), ("amount", b.Closing * sign)).Link($"ledger:{b.Id}"));
            }
            return grand;
        }

        decimal liabilities = Block(AccountNature.Liability, "LIABILITIES & CAPITAL");
        r.Rows.Add(Row(("name", pnl.NetProfit >= 0 ? "Profit for the period" : "Loss for the period"), ("total", pnl.NetProfit)).Style("header"));
        if (pnl.OpeningStock != 0) r.Rows.Add(Row(("name", "Opening stock (capital b/f)"), ("total", pnl.OpeningStock)).Style("header"));
        liabilities += pnl.NetProfit + pnl.OpeningStock;
        r.Rows.Add(Row(("name", "TOTAL LIABILITIES"), ("total", liabilities)).Style("total"));

        decimal assets = Block(AccountNature.Asset, "ASSETS");
        r.Rows.Add(Row(("name", "Closing stock"), ("total", pnl.ClosingStock)).Style("header"));
        assets += pnl.ClosingStock;
        r.Rows.Add(Row(("name", "TOTAL ASSETS"), ("total", assets)).Style("total"));
        r.Summary.Add(new("Difference", (assets - liabilities).ToString("0.00")));
        return r;
    }

    private async Task<ReportResult> ReceiptsPayments(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var cashBank = await data.Db.Ledgers.AsNoTracking()
            .Where(l => l.AccountGroup!.SystemCode == SystemGroup.CashInHand || l.AccountGroup.SystemCode == SystemGroup.BankAccounts
                        || (l.AccountGroup.Parent != null && (l.AccountGroup.Parent.SystemCode == SystemGroup.CashInHand || l.AccountGroup.Parent.SystemCode == SystemGroup.BankAccounts)))
            .Select(l => l.Id).ToListAsync(ct);
        var entries = data.Db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy && cashBank.Contains(e.LedgerId));
        decimal opening = await entries.Where(e => e.Date < start).SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0;
        var period = await entries.Where(e => e.Date >= start && e.Date < end && (e.ContraLedgerId == null || !cashBank.Contains(e.ContraLedgerId.Value)))
            .GroupBy(e => e.ContraLedger!.Name).Select(g => new { Name = g.Key, Dr = g.Sum(e => e.Debit), Cr = g.Sum(e => e.Credit) }).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Receipts & Payments Account", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            Columns = [new("name", "Particulars"), new("receipts", "Receipts", ColType.Money), new("payments", "Payments", ColType.Money)],
        };
        r.Rows.Add(Row(("name", "Opening cash & bank balance"), ("receipts", opening)).Style("subtotal"));
        foreach (var x in period.OrderBy(x => x.Name)) r.Rows.Add(Row(("name", x.Name ?? "(multiple)"), ("receipts", x.Dr), ("payments", x.Cr)));
        decimal closing = opening + period.Sum(x => x.Dr - x.Cr);
        r.Rows.Add(Row(("name", "Closing cash & bank balance"), ("payments", closing)).Style("subtotal"));
        r.Rows.Add(Row(("name", "TOTAL"), ("receipts", opening + period.Sum(x => x.Dr)), ("payments", period.Sum(x => x.Cr) + closing)).Style("total"));
        return r;
    }

    private async Task<List<(BillOutstanding Bill, string Party, decimal Balance, int Age)>> OpenBillsAsync(ReportFilter f, CancellationToken ct)
    {
        var today = data.Clock.Today();
        var q = data.Db.BillOutstandings.AsNoTracking().Include(b => b.Ledger).Where(b => !b.Cancelled && b.Settled < b.Amount);
        if (f.Side == "Payable") q = q.Where(b => b.Side == EntrySide.Cr);
        else q = q.Where(b => b.Side == EntrySide.Dr);
        if (f.PartyId is not null) q = q.Where(b => b.LedgerId == f.PartyId);
        if (f.AreaId is not null) q = q.Where(b => b.Ledger!.AreaId == f.AreaId);
        var bills = await q.ToListAsync(ct);
        return bills.Select(b => (b, b.Ledger!.Name, b.Amount - b.Settled, today.DayNumber - data.Clock.BusinessDate(b.Date).DayNumber)).ToList();
    }

    private async Task<ReportResult> Ageing(ReportFilter f, CancellationToken ct)
    {
        var bills = await OpenBillsAsync(f, ct);
        var r = new ReportResult
        {
            Title = f.Side == "Payable" ? "Payables Ageing" : "Receivables Ageing", Subtitle = $"As on {data.Clock.Today():dd-MM-yyyy}", Landscape = true,
            Columns = [new("party", "Party"), new("b0", "0-30", ColType.Money, true), new("b1", "31-60", ColType.Money, true), new("b2", "61-90", ColType.Money, true),
                new("b3", "91-180", ColType.Money, true), new("b4", ">180", ColType.Money, true), new("total", "Total", ColType.Money, true)],
        };
        foreach (var g in bills.GroupBy(b => (b.Bill.LedgerId, b.Party)).OrderBy(g => g.Key.Party))
        {
            decimal B(int lo, int hi) => g.Where(b => b.Age >= lo && b.Age <= hi).Sum(b => b.Balance);
            r.Rows.Add(Row(("party", g.Key.Party), ("b0", B(int.MinValue, 30)), ("b1", B(31, 60)), ("b2", B(61, 90)), ("b3", B(91, 180)),
                ("b4", B(181, int.MaxValue)), ("total", g.Sum(b => b.Balance))).Link($"ledger:{g.Key.LedgerId}"));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> OutstandingBills(ReportFilter f, CancellationToken ct)
    {
        var bills = await OpenBillsAsync(f, ct);
        var r = new ReportResult
        {
            Title = f.Side == "Payable" ? "Outstanding Payable Bills" : "Outstanding Receivable Bills", Landscape = true,
            Columns = [new("party", "Party"), new("no", "Bill No"), new("date", "Date", ColType.Date), new("due", "Due", ColType.Date),
                new("amount", "Bill Amount", ColType.Money, true), new("settled", "Settled", ColType.Money, true), new("balance", "Balance", ColType.Money, true),
                new("age", "Age (days)", ColType.Number)],
        };
        foreach (var b in bills.OrderBy(b => b.Party).ThenBy(b => b.Bill.Date))
            r.Rows.Add(Row(("party", b.Party), ("no", b.Bill.DocumentNo), ("date", data.D(b.Bill.Date)), ("due", b.Bill.DueDate?.ToString("yyyy-MM-dd")),
                ("amount", b.Bill.Amount), ("settled", b.Bill.Settled), ("balance", b.Balance), ("age", b.Age))
                .Link(b.Bill.TradeDocumentId is not null ? $"document:{b.Bill.TradeDocumentId}" : $"voucher:{b.Bill.VoucherId}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> CreditSummary(ReportFilter f, CancellationToken ct)
    {
        var type = f.Side == "Payable" ? PartyType.Supplier : PartyType.Customer;
        long fy = data.FinancialYearId;
        var rows = await data.Db.Ledgers.AsNoTracking().Where(l => l.PartyType == type && (f.AreaId == null || l.AreaId == f.AreaId))
            .Select(l => new
            {
                l.Id, l.Name, l.Mobile, l.CreditLimit, l.CreditDays,
                Bal = data.Db.AccountEntries.Where(e => e.LedgerId == l.Id && e.FinancialYearId == fy).Sum(e => (decimal?)(e.Debit - e.Credit)) ?? 0,
            }).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = type == PartyType.Customer ? "Customer Credit Summary" : "Supplier Balance Summary",
            Columns = [new("party", "Party"), new("mobile", "Mobile"), new("limit", "Credit Limit", ColType.Money), new("days", "Credit Days", ColType.Number),
                new("balance", "Balance", ColType.Money, true), new("available", "Available", ColType.Money), new("status", "Status")],
        };
        foreach (var x in rows.Where(x => x.Bal != 0 || x.CreditLimit > 0).OrderByDescending(x => Math.Abs(x.Bal)))
        {
            decimal bal = type == PartyType.Customer ? x.Bal : -x.Bal;
            var row = Row(("party", x.Name), ("mobile", x.Mobile), ("limit", x.CreditLimit), ("days", x.CreditDays), ("balance", bal),
                ("available", x.CreditLimit > 0 ? x.CreditLimit - bal : null), ("status", x.CreditLimit > 0 && bal > x.CreditLimit ? "Over limit" : "")).Link($"ledger:{x.Id}");
            r.Rows.Add(x.CreditLimit > 0 && bal > x.CreditLimit ? row.Style("danger") : row);
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> GroupSummary(ReportFilter f, CancellationToken ct)
    {
        var (_, to) = await data.RangeAsync(f, ct);
        var bals = await BalancesAsync(null, to, ct);
        var groups = await GroupsAsync(ct);
        var r = new ReportResult
        {
            Title = "Group Summary", Subtitle = $"As on {to:dd-MM-yyyy}",
            Columns = [new("name", "Group / Ledger"), new("dr", "Debit", ColType.Money, true), new("cr", "Credit", ColType.Money, true)],
        };
        var selected = f.GroupId is null ? groups.Values.ToList() : groups.Values.Where(g => IsUnder(g, f.GroupId.Value, groups)).ToList();
        foreach (var g in selected.OrderBy(g => g.Nature).ThenBy(g => g.Name))
        {
            var ls = bals.Where(b => b.GroupId == g.Id && b.Closing != 0).ToList();
            if (ls.Count == 0) continue;
            decimal net = ls.Sum(b => b.Closing);
            r.Rows.Add(Row(("name", g.Name), ("dr", net > 0 ? net : 0m), ("cr", net < 0 ? -net : 0m)).Style("header"));
            foreach (var b in ls.OrderBy(b => b.Name))
                r.Rows.Add(Row(("name", "    " + b.Name), ("dr", b.Closing > 0 ? b.Closing : 0m), ("cr", b.Closing < 0 ? -b.Closing : 0m)).Link($"ledger:{b.Id}"));
        }
        var dataRows = r.Rows.Where(x => !x.ContainsKey(ReportResult.StyleKey)).ToList();
        r.Totals = Row(("name", "Total"), ("dr", dataRows.Sum(x => (decimal)x["dr"]!)), ("cr", dataRows.Sum(x => (decimal)x["cr"]!)));
        return r;
    }

    private async Task<ReportResult> AccountList(ReportFilter f, CancellationToken ct)
    {
        var groups = await GroupsAsync(ct);
        var ledgers = await data.Db.Ledgers.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
        if (f.GroupId is not null) ledgers = ledgers.Where(l => IsUnder(groups[l.AccountGroupId], f.GroupId.Value, groups)).ToList();
        var r = new ReportResult
        {
            Title = "Account List", Landscape = true,
            Columns = [new("code", "Code"), new("name", "Name"), new("group", "Group"), new("mobile", "Mobile"), new("gstin", "GSTIN"), new("city", "City"), new("active", "Active")],
        };
        foreach (var l in ledgers)
            r.Rows.Add(Row(("code", l.Code), ("name", l.Name), ("group", groups[l.AccountGroupId].Name), ("mobile", l.Mobile ?? l.Phone), ("gstin", l.Gstin),
                ("city", l.City), ("active", l.IsActive ? "Yes" : "No")).Link($"ledger:{l.Id}"));
        return r;
    }

    private async Task<ReportResult> ChequeRegister(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        var q = data.Db.Vouchers.AsNoTracking().Where(v => v.ChequeNo != null && v.ChequeNo != "" && !v.Cancelled && v.Date >= start && v.Date < end);
        if (f.LedgerId is not null) q = q.Where(v => v.HeaderLedgerId == f.LedgerId || v.Lines.Any(l => l.LedgerId == f.LedgerId));
        var rows = await q.OrderBy(v => v.ChequeDate ?? DateOnly.MinValue)
            .Select(v => new { v.Id, v.Date, v.VoucherType, v.DocumentNo, v.ChequeNo, v.ChequeDate, v.Bank, v.ChequeStatus, v.IsReconciled, v.ReconciledOn, v.Total,
                Party = v.Lines.OrderBy(l => l.LineNo).Select(l => l.Ledger!.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Cheque Register", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("date", "Voucher Date", ColType.Date), new("type", "Type"), new("no", "Voucher"), new("party", "Party"), new("cheque", "Cheque No"),
                new("cdate", "Cheque Date", ColType.Date), new("bank", "Bank"), new("amount", "Amount", ColType.Money, true), new("status", "Status"),
                new("recon", "Reconciled On", ColType.Date)],
        };
        foreach (var v in rows)
            r.Rows.Add(Row(("date", data.D(v.Date)), ("type", v.VoucherType.DisplayName()), ("no", v.DocumentNo), ("party", v.Party), ("cheque", v.ChequeNo),
                ("cdate", v.ChequeDate?.ToString("yyyy-MM-dd")), ("bank", v.Bank), ("amount", v.Total), ("status", v.ChequeStatus.ToString()),
                ("recon", v.ReconciledOn?.ToString("yyyy-MM-dd"))).Link($"voucher:{v.Id}"));
        return r.ComputeTotals();
    }
}
