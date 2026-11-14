using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using static Kshethra.Application.Reports.RowExt;

namespace Kshethra.Application.Reports;

public class StockReports(ReportData data) : IReportProvider
{
    private static readonly string[] ItemFilters = [F.AsOn, F.Category, F.Manufacturer, F.Product, F.ItemClass];

    public IEnumerable<ReportDefinition> Definitions =>
    [
        new("stock-summary", "Stock Summary (Valuation)", "Stock", Screens.StockReports, ItemFilters, "On-hand quantity and value at cost"),
        new("stock-batch", "Stock Detail (Batch-wise)", "Stock", Screens.StockReports, ItemFilters),
        new("stock-category", "Category-wise Stock", "Stock", Screens.StockReports, [F.AsOn]),
        new("stock-manufacturer", "Manufacturer-wise Stock", "Stock", Screens.StockReports, [F.AsOn]),
        new("stock-supplier", "Supplier-wise Stock", "Stock", Screens.StockReports, [F.AsOn, F.Supplier]),
        new("stock-movement", "Stock Movement (Opening / In / Out / Closing)", "Stock", Screens.StockReports, [F.Dates, F.Category, F.Product]),
        new("stock-history", "Stock History (Item Ledger)", "Stock", Screens.StockReports, [F.Dates, F.Product]),
        new("stock-consumption", "Stock Consumption", "Stock", Screens.StockReports, [F.Dates, F.Category, F.Product]),
        new("stock-negative", "Negative Stock", "Stock", Screens.StockReports, [F.AsOn, F.Category]),
        new("stock-reorder", "Reorder Level (ROL) Report", "Stock", Screens.StockReports, [F.Category, F.Manufacturer]),
        new("stock-expiry", "Expiry Report", "Stock", Screens.StockReports, [F.Days, F.Category]),
        new("item-ageing", "Item Ageing (Days Since Last Sale)", "Stock", Screens.StockReports, [F.Category, F.Days]),
    ];

    public async Task<ReportResult> RunAsync(string key, ReportFilter f, CancellationToken ct)
    {
        var r = key switch
        {
            "stock-summary" => await Summary(f, ct),
            "stock-batch" => await Batch(f, ct),
            "stock-category" => await Grouped(f, "Category", x => x.Category ?? "(none)", ct),
            "stock-manufacturer" => await Grouped(f, "Manufacturer", x => x.Manufacturer ?? "(none)", ct),
            "stock-supplier" => await Supplier(f, ct),
            "stock-movement" => await Movement(f, ct),
            "stock-history" => await History(f, ct),
            "stock-consumption" => await Consumption(f, ct),
            "stock-negative" => await Negative(f, ct),
            "stock-reorder" => await Reorder(f, ct),
            "stock-expiry" => await Expiry(f, ct),
            "item-ageing" => await ItemAgeing(f, ct),
            _ => throw new NotFoundException($"Unknown report {key}."),
        };
        r.Key = key;
        return r;
    }

    private sealed record BatchQty(long ProductId, string Code, string Name, string? Category, string? Manufacturer, string Unit,
        decimal Rol, long BatchId, string Batch, decimal Mrp, decimal SaleRate, decimal Cost, DateOnly? Expiry, decimal Qty);

    /// <summary>On-hand per batch as on the "to" date within the active year.</summary>
    private async Task<List<BatchQty>> OnHandAsync(ReportFilter f, CancellationToken ct)
    {
        var (_, to) = await data.RangeAsync(f, ct);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var moves = data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.Date < end);
        var q = from p in data.Db.Products.AsNoTracking()
                join b in data.Db.Batches.AsNoTracking() on p.Id equals b.ProductId
                where p.ItemClass == ItemClass.Goods
                select new
                {
                    p, b, Cat = p.Category != null ? p.Category.Name : null, Mnf = p.Manufacturer != null ? p.Manufacturer.Name : null,
                    Qty = moves.Where(m => m.BatchId == b.Id).Sum(m => (decimal?)(m.QtyIn - m.QtyOut)) ?? 0,
                };
        if (f.CategoryId is not null) q = q.Where(x => x.p.CategoryId == f.CategoryId);
        if (f.ManufacturerId is not null) q = q.Where(x => x.p.ManufacturerId == f.ManufacturerId);
        if (f.ProductId is not null) q = q.Where(x => x.p.Id == f.ProductId);
        var rows = await q.Select(x => new BatchQty(x.p.Id, x.p.Code, x.p.Name, x.Cat, x.Mnf, x.p.MainUnit, x.p.ReorderLevel, x.b.Id, x.b.Code,
            x.b.Mrp, x.b.SaleRate, x.b.Cost > 0 ? x.b.Cost : x.p.PurchaseRate, x.b.ExpiryDate, x.Qty)).ToListAsync(ct);
        return rows;
    }

    private async Task<ReportResult> Summary(ReportFilter f, CancellationToken ct)
    {
        var rows = await OnHandAsync(f, ct);
        var (_, to) = await data.RangeAsync(f, ct);
        var r = new ReportResult
        {
            Title = "Stock Summary", Subtitle = $"As on {to:dd-MM-yyyy}",
            Columns = [new("code", "Code"), new("name", "Item"), new("category", "Category"), new("unit", "Unit"), new("qty", "Qty", ColType.Qty, true),
                new("cost", "Avg Cost", ColType.Money), new("value", "Stock Value", ColType.Money, true), new("mrpvalue", "MRP Value", ColType.Money, true)],
        };
        foreach (var g in rows.GroupBy(x => x.ProductId).Where(g => f.Detailed || g.Sum(x => x.Qty) != 0).OrderBy(g => g.First().Name))
        {
            decimal qty = g.Sum(x => x.Qty), value = Math.Round(g.Sum(x => x.Qty * x.Cost), 2);
            r.Rows.Add(Row(("code", g.First().Code), ("name", g.First().Name), ("category", g.First().Category), ("unit", g.First().Unit), ("qty", qty),
                ("cost", qty == 0 ? 0 : Math.Round(value / qty, 4)), ("value", value), ("mrpvalue", Math.Round(g.Sum(x => x.Qty * x.Mrp), 2))));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Batch(ReportFilter f, CancellationToken ct)
    {
        var rows = await OnHandAsync(f, ct);
        var (_, to) = await data.RangeAsync(f, ct);
        var r = new ReportResult
        {
            Title = "Stock Detail (Batch-wise)", Subtitle = $"As on {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("code", "Code"), new("name", "Item"), new("batch", "Batch / Barcode"), new("expiry", "Expiry", ColType.Date), new("mrp", "MRP", ColType.Money),
                new("srate", "Sale Rate", ColType.Money), new("cost", "Cost", ColType.Money), new("qty", "Qty", ColType.Qty, true), new("value", "Value", ColType.Money, true)],
        };
        foreach (var x in rows.Where(x => f.Detailed || x.Qty != 0).OrderBy(x => x.Name).ThenBy(x => x.Batch))
            r.Rows.Add(Row(("code", x.Code), ("name", x.Name), ("batch", x.Batch), ("expiry", x.Expiry?.ToString("yyyy-MM-dd")), ("mrp", x.Mrp),
                ("srate", x.SaleRate), ("cost", x.Cost), ("qty", x.Qty), ("value", Math.Round(x.Qty * x.Cost, 2))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Grouped(ReportFilter f, string title, Func<BatchQty, string> key, CancellationToken ct)
    {
        var rows = await OnHandAsync(f, ct);
        var (_, to) = await data.RangeAsync(f, ct);
        var r = new ReportResult
        {
            Title = $"{title}-wise Stock", Subtitle = $"As on {to:dd-MM-yyyy}",
            Columns = [new("name", title), new("items", "Items", ColType.Number, true), new("qty", "Qty", ColType.Qty, true), new("value", "Value", ColType.Money, true),
                new("mrpvalue", "MRP Value", ColType.Money, true)],
        };
        foreach (var g in rows.GroupBy(key).OrderBy(g => g.Key))
            r.Rows.Add(Row(("name", g.Key), ("items", g.Where(x => x.Qty != 0).Select(x => x.ProductId).Distinct().Count()), ("qty", g.Sum(x => x.Qty)),
                ("value", Math.Round(g.Sum(x => x.Qty * x.Cost), 2)), ("mrpvalue", Math.Round(g.Sum(x => x.Qty * x.Mrp), 2))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Supplier(ReportFilter f, CancellationToken ct)
    {
        var rows = await OnHandAsync(f, ct);
        long fy = data.FinancialYearId;
        // Supplier of a batch = party of its most recent stock-in from a purchase / opening document.
        var lastSupplier = await data.Db.StockMovements.AsNoTracking()
            .Where(m => m.FinancialYearId == fy && m.QtyIn > 0 && (m.VoucherType == VoucherType.Purchase || m.VoucherType == VoucherType.LocalPurchase) && m.LedgerId != null)
            .GroupBy(m => m.BatchId).Select(g => new { g.Key, LedgerId = g.OrderByDescending(m => m.Date).Select(m => m.LedgerId).First() })
            .ToDictionaryAsync(x => x.Key, x => x.LedgerId, ct);
        var names = await data.Db.Ledgers.AsNoTracking().Where(l => l.PartyType == PartyType.Supplier).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var r = new ReportResult
        {
            Title = "Supplier-wise Stock", Columns = [new("supplier", "Supplier"), new("item", "Item"), new("batch", "Batch"), new("qty", "Qty", ColType.Qty, true), new("value", "Value", ColType.Money, true)],
        };
        foreach (var x in rows.Where(x => x.Qty != 0)
                     .Select(x => (x, Sup: lastSupplier.TryGetValue(x.BatchId, out var lid) && lid is not null ? lid : null))
                     .Where(t => f.PartyId == null || t.Sup == f.PartyId)
                     .OrderBy(t => t.Sup is null ? "~" : names.GetValueOrDefault(t.Sup.Value)).ThenBy(t => t.x.Name))
            r.Rows.Add(Row(("supplier", x.Sup is null ? "(unknown)" : names.GetValueOrDefault(x.Sup.Value)), ("item", x.x.Name), ("batch", x.x.Batch),
                ("qty", x.x.Qty), ("value", Math.Round(x.x.Qty * x.x.Cost, 2))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Movement(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var q = data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.Date < end);
        if (f.ProductId is not null) q = q.Where(m => m.ProductId == f.ProductId);
        if (f.CategoryId is not null) q = q.Where(m => m.Product!.CategoryId == f.CategoryId);
        var agg = await q.GroupBy(m => new { m.ProductId, m.Product!.Code, m.Product.Name })
            .Select(g => new
            {
                g.Key.ProductId, g.Key.Code, g.Key.Name,
                Open = g.Where(m => m.Date < start).Sum(m => m.QtyIn - m.QtyOut),
                In = g.Where(m => m.Date >= start).Sum(m => m.QtyIn),
                Out = g.Where(m => m.Date >= start).Sum(m => m.QtyOut),
            }).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Stock Movement", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            Columns = [new("code", "Code"), new("name", "Item"), new("open", "Opening", ColType.Qty, true), new("in", "Inward", ColType.Qty, true),
                new("out", "Outward", ColType.Qty, true), new("close", "Closing", ColType.Qty, true)],
        };
        foreach (var x in agg.OrderBy(x => x.Name))
            r.Rows.Add(Row(("code", x.Code), ("name", x.Name), ("open", x.Open), ("in", x.In), ("out", x.Out), ("close", x.Open + x.In - x.Out)));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> History(ReportFilter f, CancellationToken ct)
    {
        if (f.ProductId is null) throw new DomainException("Select an item.");
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var all = data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.ProductId == f.ProductId);
        decimal opening = await all.Where(m => m.Date < start).SumAsync(m => (decimal?)(m.QtyIn - m.QtyOut), ct) ?? 0;
        var moves = await all.Where(m => m.Date >= start && m.Date < end).OrderBy(m => m.Date).ThenBy(m => m.Id)
            .Select(m => new { m.Date, m.VoucherType, m.DocumentNo, m.TradeDocumentId, Batch = m.Batch!.Code, m.QtyIn, m.QtyOut, m.Cost, m.LedgerId }).ToListAsync(ct);
        var parties = await data.Db.Ledgers.AsNoTracking().Where(l => moves.Select(m => m.LedgerId).Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var name = await data.Db.Products.Where(p => p.Id == f.ProductId).Select(p => p.Name).FirstAsync(ct);
        var r = new ReportResult
        {
            Title = $"Stock History — {name}", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("type", "Type"), new("no", "Doc No"), new("party", "Party"), new("batch", "Batch"),
                new("in", "In", ColType.Qty, true), new("out", "Out", ColType.Qty, true), new("balance", "Balance", ColType.Qty), new("cost", "Cost", ColType.Money)],
        };
        r.Rows.Add(Row(("date", from.ToString("yyyy-MM-dd")), ("type", "Opening"), ("balance", opening)).Style("subtotal"));
        decimal bal = opening;
        foreach (var m in moves)
        {
            bal += m.QtyIn - m.QtyOut;
            r.Rows.Add(Row(("date", data.D(m.Date)), ("type", m.VoucherType.DisplayName()), ("no", m.DocumentNo),
                ("party", m.LedgerId is null ? null : parties.GetValueOrDefault(m.LedgerId.Value)), ("batch", m.Batch), ("in", m.QtyIn), ("out", m.QtyOut),
                ("balance", bal), ("cost", m.Cost)).Link($"document:{m.TradeDocumentId}"));
        }
        r.ComputeTotals();
        r.Summary.Add(new("Closing balance", bal.ToString("0.###")));
        return r;
    }

    private async Task<ReportResult> Consumption(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var q = data.Db.StockMovements.AsNoTracking().Where(m => m.FinancialYearId == fy && m.Date >= start && m.Date < end && m.QtyOut > 0);
        if (f.ProductId is not null) q = q.Where(m => m.ProductId == f.ProductId);
        if (f.CategoryId is not null) q = q.Where(m => m.Product!.CategoryId == f.CategoryId);
        var agg = await q.GroupBy(m => new { m.Product!.Code, m.Product.Name, m.VoucherType })
            .Select(g => new { g.Key.Code, g.Key.Name, g.Key.VoucherType, Qty = g.Sum(m => m.QtyOut), Value = g.Sum(m => m.QtyOut * m.Cost) }).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Stock Consumption", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            Columns = [new("code", "Code"), new("name", "Item"), new("type", "Through"), new("qty", "Qty Out", ColType.Qty, true), new("value", "Cost Value", ColType.Money, true)],
        };
        foreach (var x in agg.OrderBy(x => x.Name).ThenBy(x => x.VoucherType))
            r.Rows.Add(Row(("code", x.Code), ("name", x.Name), ("type", x.VoucherType.DisplayName()), ("qty", x.Qty), ("value", Math.Round(x.Value, 2))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Negative(ReportFilter f, CancellationToken ct)
    {
        var rows = (await OnHandAsync(f, ct)).Where(x => x.Qty < 0).OrderBy(x => x.Name).ToList();
        var r = new ReportResult
        {
            Title = "Negative Stock",
            Columns = [new("code", "Code"), new("name", "Item"), new("batch", "Batch"), new("qty", "Qty", ColType.Qty, true), new("value", "Value", ColType.Money, true)],
        };
        foreach (var x in rows) r.Rows.Add(Row(("code", x.Code), ("name", x.Name), ("batch", x.Batch), ("qty", x.Qty), ("value", Math.Round(x.Qty * x.Cost, 2))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Reorder(ReportFilter f, CancellationToken ct)
    {
        var rows = await OnHandAsync(new ReportFilter { CategoryId = f.CategoryId, ManufacturerId = f.ManufacturerId }, ct);
        var products = await data.Db.Products.AsNoTracking().Where(p => p.IsActive && p.ReorderLevel > 0 && p.ItemClass == ItemClass.Goods
                                                                        && (f.CategoryId == null || p.CategoryId == f.CategoryId)
                                                                        && (f.ManufacturerId == null || p.ManufacturerId == f.ManufacturerId))
            .Select(p => new { p.Id, p.Code, p.Name, p.MainUnit, p.ReorderLevel, p.PurchaseRate }).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Reorder Level Report",
            Columns = [new("code", "Code"), new("name", "Item"), new("unit", "Unit"), new("rol", "ROL", ColType.Qty), new("stock", "Stock", ColType.Qty),
                new("order", "Suggested Order", ColType.Qty, true), new("value", "Order Value", ColType.Money, true)],
        };
        foreach (var p in products.OrderBy(p => p.Name))
        {
            decimal stockQty = rows.Where(x => x.ProductId == p.Id).Sum(x => x.Qty);
            if (stockQty > p.ReorderLevel) continue;
            decimal order = p.ReorderLevel * 2 - stockQty;
            r.Rows.Add(Row(("code", p.Code), ("name", p.Name), ("unit", p.MainUnit), ("rol", p.ReorderLevel), ("stock", stockQty), ("order", order),
                ("value", Math.Round(order * p.PurchaseRate, 2))));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Expiry(ReportFilter f, CancellationToken ct)
    {
        int days = f.Days ?? 30;
        var today = data.Clock.Today();
        var limit = today.AddDays(days);
        var rows = (await OnHandAsync(new ReportFilter { CategoryId = f.CategoryId }, ct))
            .Where(x => x.Expiry is not null && x.Expiry <= limit && x.Qty > 0).OrderBy(x => x.Expiry).ToList();
        var r = new ReportResult
        {
            Title = "Expiry Report", Subtitle = $"Expired or expiring within {days} days of {today:dd-MM-yyyy}",
            Columns = [new("code", "Code"), new("name", "Item"), new("batch", "Batch"), new("expiry", "Expiry", ColType.Date), new("days", "Days Left", ColType.Number),
                new("qty", "Qty", ColType.Qty, true), new("value", "Value", ColType.Money, true)],
        };
        foreach (var x in rows)
        {
            int left = x.Expiry!.Value.DayNumber - today.DayNumber;
            var row = Row(("code", x.Code), ("name", x.Name), ("batch", x.Batch), ("expiry", x.Expiry.Value.ToString("yyyy-MM-dd")), ("days", left),
                ("qty", x.Qty), ("value", Math.Round(x.Qty * x.Cost, 2)));
            r.Rows.Add(left < 0 ? row.Style("danger") : row);
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> ItemAgeing(ReportFilter f, CancellationToken ct)
    {
        var rows = (await OnHandAsync(new ReportFilter { CategoryId = f.CategoryId }, ct)).Where(x => x.Qty > 0).ToList();
        var ids = rows.Select(x => x.ProductId).Distinct().ToList();
        var lastSale = await data.Db.StockMovements.AsNoTracking()
            .Where(m => ids.Contains(m.ProductId) && m.QtyOut > 0 && (m.VoucherType == VoucherType.Sales || m.VoucherType == VoucherType.LocalSales || m.VoucherType == VoucherType.POSSales))
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, Last = g.Max(m => m.Date) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var lastIn = await data.Db.StockMovements.AsNoTracking().Where(m => ids.Contains(m.ProductId) && m.QtyIn > 0)
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, Last = g.Max(m => m.Date) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var today = data.Clock.Today();
        int minDays = f.Days ?? 0;
        var r = new ReportResult
        {
            Title = "Item Ageing", Subtitle = minDays > 0 ? $"Items not sold for {minDays}+ days" : null,
            Columns = [new("code", "Code"), new("name", "Item"), new("stock", "Stock", ColType.Qty, true), new("value", "Value", ColType.Money, true),
                new("lastin", "Last Inward", ColType.Date), new("lastsale", "Last Sale", ColType.Date), new("days", "Days Since Sale", ColType.Number),
                new("bucket", "Bucket")],
        };
        foreach (var g in rows.GroupBy(x => x.ProductId))
        {
            var ls = lastSale.TryGetValue(g.Key, out var s) ? data.Clock.BusinessDate(s) : (DateOnly?)null;
            var li = lastIn.TryGetValue(g.Key, out var i) ? data.Clock.BusinessDate(i) : (DateOnly?)null;
            int age = today.DayNumber - (ls ?? li ?? today).DayNumber;
            if (age < minDays) continue;
            string bucket = age switch { <= 30 => "0-30", <= 60 => "31-60", <= 90 => "61-90", <= 180 => "91-180", _ => ">180" };
            r.Rows.Add(Row(("code", g.First().Code), ("name", g.First().Name), ("stock", g.Sum(x => x.Qty)), ("value", Math.Round(g.Sum(x => x.Qty * x.Cost), 2)),
                ("lastin", li?.ToString("yyyy-MM-dd")), ("lastsale", ls?.ToString("yyyy-MM-dd")), ("days", age), ("bucket", bucket)));
        }
        r.Rows = r.Rows.OrderByDescending(x => x["days"]).ToList();
        return r.ComputeTotals();
    }
}
