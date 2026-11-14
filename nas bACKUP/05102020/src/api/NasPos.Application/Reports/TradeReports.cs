using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using static NasPos.Application.Reports.RowExt;

namespace NasPos.Application.Reports;

/// <summary>Sales, purchase, counter and profit reports.</summary>
public class TradeReports(ReportData data) : IReportProvider
{
    private static readonly string[] SaleFilters = [F.Dates, F.VoucherType, F.PaymentMode, F.TaxType, F.Customer, F.Salesperson, F.Counter, F.User, F.Area];
    private static readonly string[] SaleItemFilters = [F.Dates, F.VoucherType, F.PaymentMode, F.Customer, F.Category, F.Product, F.Manufacturer, F.ItemClass, F.Salesperson];
    private static readonly string[] PurchaseFilters = [F.Dates, F.VoucherType, F.PaymentMode, F.TaxType, F.Supplier];

    public IEnumerable<ReportDefinition> Definitions =>
    [
        new("sales-daybook", "Sales Day Book", "Sales", Screens.SalesReports, SaleFilters, "Bill-wise sales register"),
        new("sales-detail", "Sales Detail (Item-wise Bills)", "Sales", Screens.SalesReports, SaleItemFilters),
        new("sales-product", "Product-wise Sales", "Sales", Screens.SalesReports, SaleItemFilters),
        new("sales-category", "Category-wise Sales", "Sales", Screens.SalesReports, SaleItemFilters),
        new("sales-manufacturer", "Manufacturer-wise Sales", "Sales", Screens.SalesReports, SaleItemFilters),
        new("sales-area", "Area-wise Sales", "Sales", Screens.SalesReports, [F.Dates, F.Area]),
        new("sales-customer", "Customer-wise Sales", "Sales", Screens.SalesReports, [F.Dates, F.PaymentMode, F.Area]),
        new("sales-daily", "Daily Sales Summary", "Sales", Screens.SalesReports, [F.Dates, F.VoucherType]),
        new("sales-returns", "Sales Returns", "Sales", Screens.SalesReports, [F.Dates, F.Customer]),
        new("sales-orders", "Orders & Estimates", "Sales", Screens.SalesReports, [F.Dates, F.VoucherType, F.Party]),
        new("delivery-notes", "Delivery / Receipt Notes", "Sales", Screens.SalesReports, [F.Dates, F.VoucherType, F.Party]),
        new("salesperson-summary", "Salesperson Summary & Commission", "Sales", Screens.SalesReports, [F.Dates, F.Salesperson]),
        new("counter-summary", "Counter Summary", "Counter", Screens.SalesReports, [F.Dates, F.Counter, F.User]),
        new("counter-detail", "Counter Detail", "Counter", Screens.SalesReports, [F.Dates, F.Counter, F.User]),
        new("end-of-day", "End of Day", "Counter", Screens.SalesReports, [F.Dates, F.Counter]),
        new("void-report", "Void Report (Cancelled Bills & Removed Items)", "Counter", Screens.SalesReports, [F.Dates, F.User]),
        new("purchase-daybook", "Purchase Day Book", "Purchase", Screens.PurchaseReports, PurchaseFilters),
        new("purchase-detail", "Purchase Detail (Item-wise)", "Purchase", Screens.PurchaseReports, [F.Dates, F.Supplier, F.Category, F.Product, F.Manufacturer]),
        new("purchase-product", "Product-wise Purchase", "Purchase", Screens.PurchaseReports, [F.Dates, F.Supplier, F.Category, F.Manufacturer]),
        new("purchase-supplier", "Supplier-wise Purchase", "Purchase", Screens.PurchaseReports, [F.Dates]),
        new("purchase-category", "Category-wise Purchase", "Purchase", Screens.PurchaseReports, [F.Dates, F.Supplier]),
        new("purchase-costfactor", "Purchase Cost Analysis", "Purchase", Screens.PurchaseReports, [F.Dates, F.Supplier, F.Product]),
        new("purchase-returns", "Purchase Returns", "Purchase", Screens.PurchaseReports, [F.Dates, F.Supplier]),
        new("bill-profit", "Bill-wise Profit", "Analysis", Screens.SalesReports, SaleFilters),
        new("product-profit", "Product-wise Profit", "Analysis", Screens.SalesReports, SaleItemFilters),
        new("category-profit", "Category-wise Profit", "Analysis", Screens.SalesReports, [F.Dates, F.Category]),
        new("production-consumed", "Production: Materials Consumed", "Analysis", Screens.StockReports, [F.Dates, F.Product]),
        new("production-generated", "Production: Goods Generated", "Analysis", Screens.StockReports, [F.Dates, F.Product]),
        new("price-list", "Price List", "Analysis", Screens.SalesReports, [F.Category]),
        new("loyalty-points", "Customer Loyalty Points", "Analysis", Screens.SalesReports, [F.Dates, F.Customer]),
    ];

    public async Task<ReportResult> RunAsync(string key, ReportFilter f, CancellationToken ct)
    {
        var r = key switch
        {
            "sales-daybook" => await DayBook(ReportData.SalesTypes, "Sales Day Book", f, ct),
            "purchase-daybook" => await DayBook(ReportData.PurchaseTypes, "Purchase Day Book", f, ct),
            "sales-returns" => await DayBook([VoucherType.SalesReturn], "Sales Returns", f, ct),
            "purchase-returns" => await DayBook([VoucherType.PurchaseReturn], "Purchase Returns", f, ct),
            "sales-orders" => await Orders(f, ct),
            "delivery-notes" => await DayBook([VoucherType.DeliveryNote, VoucherType.ReceiptNote], "Delivery / Receipt Notes", f, ct),
            "sales-detail" => await Detail(ReportData.SalesTypes, "Sales Detail", f, ct),
            "purchase-detail" => await Detail(ReportData.PurchaseTypes, "Purchase Detail", f, ct),
            "sales-product" => await ByItem(ReportData.SalesTypes, "Product-wise Sales", l => (l.ProductId.ToString(), l.ProductCode, l.ProductName), f, ct, "Code", "Product"),
            "purchase-product" => await ByItem(ReportData.PurchaseTypes, "Product-wise Purchase", l => (l.ProductId.ToString(), l.ProductCode, l.ProductName), f, ct, "Code", "Product"),
            "sales-category" => await ByItem(ReportData.SalesTypes, "Category-wise Sales", l => ((l.CategoryId ?? 0).ToString(), "", l.CategoryName ?? "(none)"), f, ct, null, "Category"),
            "purchase-category" => await ByItem(ReportData.PurchaseTypes, "Category-wise Purchase", l => ((l.CategoryId ?? 0).ToString(), "", l.CategoryName ?? "(none)"), f, ct, null, "Category"),
            "sales-manufacturer" => await ByItem(ReportData.SalesTypes, "Manufacturer-wise Sales", l => ((l.ManufacturerId ?? 0).ToString(), "", l.ManufacturerName ?? "(none)"), f, ct, null, "Manufacturer"),
            "sales-customer" => await ByParty(ReportData.SalesTypes, "Customer-wise Sales", f, ct),
            "purchase-supplier" => await ByParty(ReportData.PurchaseTypes, "Supplier-wise Purchase", f, ct),
            "sales-area" => await ByArea(f, ct),
            "sales-daily" => await Daily(f, ct),
            "salesperson-summary" => await Salesperson(f, ct),
            "counter-summary" => await Counter(f, false, ct),
            "counter-detail" => await Counter(f, true, ct),
            "end-of-day" => await EndOfDay(f, ct),
            "void-report" => await Void(f, ct),
            "purchase-costfactor" => await CostAnalysis(f, ct),
            "bill-profit" => await BillProfit(f, ct),
            "product-profit" => await ItemProfit(f, l => (l.ProductId, l.ProductName), "Product", "Product-wise Profit", ct),
            "category-profit" => await ItemProfit(f, l => (l.CategoryId ?? 0, l.CategoryName ?? "(none)"), "Category", "Category-wise Profit", ct),
            "production-consumed" => await Production(f, StockDirection.Out, "Production: Materials Consumed", ct),
            "production-generated" => await Production(f, StockDirection.In, "Production: Goods Generated", ct),
            "price-list" => await PriceList(f, ct),
            "loyalty-points" => await Loyalty(f, ct),
            _ => throw new NotFoundException($"Unknown report {key}."),
        };
        r.Key = key;
        return r;
    }

    private async Task<ReportResult> DayBook(VoucherType[] types, string title, ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(types, f, false, ct);
        var users = await data.UserNamesAsync(ct);
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns =
            [
                new("date", "Date", ColType.Date), new("no", "Bill No"), new("type", "Type"), new("party", "Party"), new("mode", "Mode"),
                new("gross", "Gross", ColType.Money, true), new("disc", "Discount", ColType.Money, true), new("taxable", "Taxable", ColType.Money, true),
                new("tax", "Tax", ColType.Money, true), new("roundoff", "Round Off", ColType.Money, true), new("amount", "Bill Amount", ColType.Money, true),
                new("user", "User"),
            ],
        };
        foreach (var d in docs)
            r.Rows.Add(Row(("date", data.D(d.Date)), ("no", d.DocumentNo), ("type", d.VoucherType.DisplayName()), ("party", d.PartyName),
                ("mode", d.PaymentMode.ToString()), ("gross", d.GrossAmount), ("disc", d.ItemDiscount + d.BillDiscount + d.CashDiscount),
                ("taxable", d.TaxableAmount + d.ExemptAmount), ("tax", d.CgstAmount + d.SgstAmount + d.IgstAmount + d.CessAmount + d.AddCessAmount + d.KfCessAmount),
                ("roundoff", d.RoundOff), ("amount", d.BillAmount), ("user", d.UserId is null ? null : users.GetValueOrDefault(d.UserId.Value)))
                .Link($"document:{d.Id}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Orders(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync([VoucherType.Estimate, VoucherType.SalesOrder, VoucherType.PurchaseOrder], f, false, ct);
        var r = new ReportResult
        {
            Title = "Orders & Estimates", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("date", "Date", ColType.Date), new("no", "No"), new("type", "Type"), new("party", "Party"), new("delivery", "Delivery", ColType.Date),
                new("amount", "Amount", ColType.Money, true), new("status", "Status")],
        };
        foreach (var d in docs)
            r.Rows.Add(Row(("date", data.D(d.Date)), ("no", d.DocumentNo), ("type", d.VoucherType.DisplayName()), ("party", d.PartyName),
                ("delivery", d.DeliveryDate?.ToString("yyyy-MM-dd")), ("amount", d.BillAmount), ("status", d.IsProcessed ? "Converted" : "Open"))
                .Link($"document:{d.Id}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Detail(VoucherType[] types, string title, ReportFilter f, CancellationToken ct)
    {
        var lines = await data.LinesAsync(types, f, ct);
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns =
            [
                new("date", "Date", ColType.Date), new("no", "Bill No"), new("party", "Party"), new("code", "Code"), new("item", "Item"),
                new("batch", "Batch"), new("qty", "Qty", ColType.Qty, true), new("free", "Free", ColType.Qty, true), new("rate", "Rate", ColType.Money),
                new("disc", "Disc", ColType.Money, true), new("taxable", "Taxable", ColType.Money, true), new("gst", "GST %", ColType.Percent),
                new("tax", "Tax", ColType.Money, true), new("total", "Total", ColType.Money, true),
            ],
        };
        foreach (var l in lines)
            r.Rows.Add(Row(("date", data.D(l.Date)), ("no", l.DocNo), ("party", l.PartyName), ("code", l.ProductCode), ("item", l.ProductName),
                ("batch", l.Batch), ("qty", l.Qty), ("free", l.Free), ("rate", l.Rate), ("disc", l.Discount), ("taxable", l.Taxable),
                ("gst", l.GstRate), ("tax", l.Tax), ("total", l.LineTotal)).Link($"document:{l.DocId}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> ByItem(VoucherType[] types, string title, Func<LineRow, (string Key, string Code, string Name)> key,
        ReportFilter f, CancellationToken ct, string? codeTitle, string nameTitle)
    {
        var lines = await data.LinesAsync(types, f, ct);
        var cols = new List<ReportColumn>();
        if (codeTitle is not null) cols.Add(new("code", codeTitle));
        cols.AddRange([new("name", nameTitle), new("qty", "Qty", ColType.Qty, true), new("free", "Free", ColType.Qty, true),
            new("gross", "Gross", ColType.Money, true), new("disc", "Discount", ColType.Money, true), new("taxable", "Taxable", ColType.Money, true),
            new("tax", "Tax", ColType.Money, true), new("total", "Total", ColType.Money, true)]);
        var r = new ReportResult { Title = title, Subtitle = await data.RangeTextAsync(f, ct), Columns = cols };
        foreach (var g in lines.GroupBy(key).OrderBy(g => g.Key.Name))
        {
            var row = Row(("name", g.Key.Name), ("qty", g.Sum(l => l.Qty * l.UnitFactor)), ("free", g.Sum(l => l.Free * l.UnitFactor)),
                ("gross", g.Sum(l => l.Gross)), ("disc", g.Sum(l => l.Discount)), ("taxable", g.Sum(l => l.Taxable)), ("tax", g.Sum(l => l.Tax)),
                ("total", g.Sum(l => l.LineTotal)));
            if (codeTitle is not null) row["code"] = g.Key.Code;
            r.Rows.Add(row);
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> ByParty(VoucherType[] types, string title, ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(types, f, false, ct);
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("party", "Party"), new("gstin", "GSTIN"), new("bills", "Bills", ColType.Number, true), new("taxable", "Taxable", ColType.Money, true),
                new("tax", "Tax", ColType.Money, true), new("amount", "Amount", ColType.Money, true), new("cash", "Cash", ColType.Money, true),
                new("credit", "Credit", ColType.Money, true)],
        };
        foreach (var g in docs.GroupBy(d => (d.PartyLedgerId, Name: d.PartyName ?? "Cash")).OrderByDescending(g => g.Sum(d => d.BillAmount)))
            r.Rows.Add(Row(("party", g.Key.Name), ("gstin", g.First().PartyGstin), ("bills", g.Count()),
                ("taxable", g.Sum(d => d.TaxableAmount + d.ExemptAmount)),
                ("tax", g.Sum(d => d.CgstAmount + d.SgstAmount + d.IgstAmount + d.CessAmount + d.AddCessAmount + d.KfCessAmount)),
                ("amount", g.Sum(d => d.BillAmount)), ("cash", g.Sum(d => d.CashAmount + d.CardAmount)), ("credit", g.Sum(d => d.CreditAmount))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> ByArea(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var areas = await data.Db.Ledgers.AsNoTracking().Where(l => l.AreaId != null)
            .Select(l => new { l.Id, Area = l.Area!.Name }).ToDictionaryAsync(x => x.Id, x => x.Area, ct);
        var r = new ReportResult
        {
            Title = "Area-wise Sales", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("area", "Area"), new("bills", "Bills", ColType.Number, true), new("taxable", "Taxable", ColType.Money, true), new("amount", "Amount", ColType.Money, true)],
        };
        foreach (var g in docs.GroupBy(d => d.PartyLedgerId is { } id && areas.TryGetValue(id, out var a) ? a : "(no area)").OrderBy(g => g.Key))
            r.Rows.Add(Row(("area", g.Key), ("bills", g.Count()), ("taxable", g.Sum(d => d.TaxableAmount + d.ExemptAmount)), ("amount", g.Sum(d => d.BillAmount))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Daily(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var returns = await data.DocsAsync([VoucherType.SalesReturn], f, false, ct);
        var r = new ReportResult
        {
            Title = "Daily Sales Summary", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("date", "Date", ColType.Date), new("bills", "Bills", ColType.Number, true), new("cash", "Cash", ColType.Money, true),
                new("card", "Card", ColType.Money, true), new("credit", "Credit", ColType.Money, true), new("sales", "Sales", ColType.Money, true),
                new("returns", "Returns", ColType.Money, true), new("net", "Net", ColType.Money, true)],
        };
        var days = docs.Select(d => data.D(d.Date)).Concat(returns.Select(d => data.D(d.Date))).Distinct().OrderBy(d => d);
        foreach (var day in days)
        {
            var s = docs.Where(d => data.D(d.Date) == day).ToList();
            var rt = returns.Where(d => data.D(d.Date) == day).Sum(d => d.BillAmount);
            r.Rows.Add(Row(("date", day), ("bills", s.Count), ("cash", s.Sum(d => d.CashAmount)), ("card", s.Sum(d => d.CardAmount)),
                ("credit", s.Sum(d => d.CreditAmount)), ("sales", s.Sum(d => d.BillAmount)), ("returns", rt), ("net", s.Sum(d => d.BillAmount) - rt)));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Salesperson(ReportFilter f, CancellationToken ct)
    {
        var lines = await data.LinesAsync(ReportData.SalesTypes, f, ct);
        var emps = await data.Db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var r = new ReportResult
        {
            Title = "Salesperson Summary & Commission", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("sp", "Salesperson"), new("bills", "Bills", ColType.Number, true), new("taxable", "Taxable", ColType.Money, true),
                new("amount", "Sales", ColType.Money, true), new("profit", "Profit", ColType.Money, true), new("commission", "Commission", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => l.SalespersonId).OrderBy(g => g.Key is null ? "" : emps.GetValueOrDefault(g.Key.Value)?.Name))
        {
            var emp = g.Key is null ? null : emps.GetValueOrDefault(g.Key.Value);
            // Item commission % wins; otherwise the salesperson's own %.
            decimal commission = g.Sum(l => Math.Round(l.Taxable * (l.CommissionPercent > 0 ? l.CommissionPercent : emp?.CommissionPercent ?? 0) / 100, 2));
            r.Rows.Add(Row(("sp", emp?.Name ?? "(none)"), ("bills", g.Select(l => l.DocId).Distinct().Count()), ("taxable", g.Sum(l => l.Taxable)),
                ("amount", g.Sum(l => l.LineTotal)), ("profit", g.Sum(l => l.Profit)), ("commission", commission)));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Counter(ReportFilter f, bool detail, CancellationToken ct)
    {
        var docs = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var users = await data.UserNamesAsync(ct);
        var r = new ReportResult
        {
            Title = detail ? "Counter Detail" : "Counter Summary", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = detail
                ? [new("counter", "Counter"), new("user", "User"), new("no", "Bill No"), new("date", "Date", ColType.Date), new("cash", "Cash", ColType.Money, true),
                    new("card", "Card", ColType.Money, true), new("credit", "Credit", ColType.Money, true), new("amount", "Amount", ColType.Money, true)]
                : [new("counter", "Counter"), new("user", "User"), new("bills", "Bills", ColType.Number, true), new("cash", "Cash", ColType.Money, true),
                    new("card", "Card", ColType.Money, true), new("credit", "Credit", ColType.Money, true), new("amount", "Amount", ColType.Money, true)],
        };
        if (detail)
            foreach (var d in docs.OrderBy(d => d.Counter).ThenBy(d => d.Date))
                r.Rows.Add(Row(("counter", d.Counter ?? "-"), ("user", d.UserId is null ? null : users.GetValueOrDefault(d.UserId.Value)), ("no", d.DocumentNo),
                    ("date", data.D(d.Date)), ("cash", d.CashAmount), ("card", d.CardAmount), ("credit", d.CreditAmount), ("amount", d.BillAmount)).Link($"document:{d.Id}"));
        else
            foreach (var g in docs.GroupBy(d => (d.Counter, d.UserId)).OrderBy(g => g.Key.Counter))
                r.Rows.Add(Row(("counter", g.Key.Counter ?? "-"), ("user", g.Key.UserId is null ? null : users.GetValueOrDefault(g.Key.UserId.Value)),
                    ("bills", g.Count()), ("cash", g.Sum(d => d.CashAmount)), ("card", g.Sum(d => d.CardAmount)), ("credit", g.Sum(d => d.CreditAmount)),
                    ("amount", g.Sum(d => d.BillAmount))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> EndOfDay(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var sales = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var returns = await data.DocsAsync([VoucherType.SalesReturn], f, false, ct);
        var cancelled = (await data.DocsAsync(ReportData.SalesTypes, f, true, ct)).Where(d => d.Cancelled).ToList();
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long cashId = await data.Db.Ledgers.Where(l => l.SystemCode == SystemLedger.Cash).Select(l => l.Id).FirstAsync(ct);
        var cashVouchers = await data.Db.AccountEntries.AsNoTracking()
            .Where(e => e.LedgerId == cashId && e.VoucherId != null && e.Date >= start && e.Date < end)
            .GroupBy(e => e.VoucherType).Select(g => new { g.Key, Dr = g.Sum(e => e.Debit), Cr = g.Sum(e => e.Credit) }).ToListAsync(ct);
        decimal receipts = cashVouchers.Sum(x => x.Dr), payments = cashVouchers.Sum(x => x.Cr);

        var r = new ReportResult
        {
            Title = "End of Day", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}" + (string.IsNullOrEmpty(f.Counter) ? "" : $" · Counter {f.Counter}"),
            Columns = [new("item", "Particulars"), new("count", "Count", ColType.Number), new("amount", "Amount", ColType.Money)],
        };
        r.Rows.Add(Row(("item", "SALES")).Style("header"));
        r.Rows.Add(Row(("item", "Cash sales"), ("count", sales.Count(d => d.CashAmount > 0)), ("amount", sales.Sum(d => d.CashAmount))));
        r.Rows.Add(Row(("item", "Card / bank sales"), ("count", sales.Count(d => d.CardAmount > 0)), ("amount", sales.Sum(d => d.CardAmount))));
        r.Rows.Add(Row(("item", "Credit sales"), ("count", sales.Count(d => d.CreditAmount > 0)), ("amount", sales.Sum(d => d.CreditAmount))));
        r.Rows.Add(Row(("item", "Total sales"), ("count", sales.Count), ("amount", sales.Sum(d => d.BillAmount))).Style("subtotal"));
        r.Rows.Add(Row(("item", "Sales returns"), ("count", returns.Count), ("amount", -returns.Sum(d => d.BillAmount))));
        r.Rows.Add(Row(("item", "Cancelled bills"), ("count", cancelled.Count), ("amount", cancelled.Sum(d => d.BillAmount))));
        r.Rows.Add(Row(("item", "Discounts given"), ("amount", sales.Sum(d => d.ItemDiscount + d.BillDiscount + d.CashDiscount))));
        r.Rows.Add(Row(("item", "Round off"), ("amount", sales.Sum(d => d.RoundOff))));
        r.Rows.Add(Row(("item", "CASH")).Style("header"));
        decimal cashReturns = returns.Where(d => d.PaymentMode == PaymentMode.Cash).Sum(d => d.BillAmount);
        r.Rows.Add(Row(("item", "Cash from sales"), ("amount", sales.Sum(d => d.CashAmount))));
        r.Rows.Add(Row(("item", "Cash receipts (vouchers)"), ("amount", receipts)));
        r.Rows.Add(Row(("item", "Cash payments (vouchers)"), ("amount", -payments)));
        r.Rows.Add(Row(("item", "Cash refunds (returns)"), ("amount", -cashReturns)));
        r.Rows.Add(Row(("item", "Net cash for the period"), ("amount", sales.Sum(d => d.CashAmount) + receipts - payments - cashReturns)).Style("total"));
        return r;
    }

    private async Task<ReportResult> Void(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        var users = await data.UserNamesAsync(ct);
        var cancelled = await data.Db.TradeDocuments.AsNoTracking().Where(d => d.Cancelled && d.CancelledAt >= start && d.CancelledAt < end).OrderBy(d => d.CancelledAt).ToListAsync(ct);
        var items = await data.Db.VoidItems.AsNoTracking().Where(v => v.At >= start && v.At < end && (f.UserId == null || v.UserId == f.UserId)).OrderBy(v => v.At).ToListAsync(ct);
        var r = new ReportResult
        {
            Title = "Void Report", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}", Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("kind", "Kind"), new("ref", "Bill / Screen"), new("detail", "Detail"), new("qty", "Qty", ColType.Qty),
                new("amount", "Amount", ColType.Money, true), new("user", "User"), new("reason", "Reason / Counter")],
        };
        foreach (var d in cancelled.Where(d => f.UserId == null || d.CancelledBy == f.UserId))
            r.Rows.Add(Row(("date", data.D(d.CancelledAt!.Value)), ("kind", "Cancelled bill"), ("ref", $"{d.VoucherType.DisplayName()} {d.DocumentNo}"),
                ("detail", d.PartyName), ("amount", d.BillAmount), ("user", d.CancelledBy is null ? null : users.GetValueOrDefault(d.CancelledBy.Value)),
                ("reason", d.CancelReason)).Link($"document:{d.Id}"));
        foreach (var v in items)
            r.Rows.Add(Row(("date", data.D(v.At)), ("kind", "Removed item"), ("ref", $"{v.Screen} {v.DocumentNo}"), ("detail", $"{v.ProductName} {v.Batch}"),
                ("qty", v.Qty), ("amount", Math.Round(v.Qty * v.Rate, 2)), ("user", v.UserName), ("reason", v.Machine)));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> CostAnalysis(ReportFilter f, CancellationToken ct)
    {
        var lines = await data.LinesAsync(ReportData.PurchaseTypes, f, ct);
        var r = new ReportResult
        {
            Title = "Purchase Cost Analysis", Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("no", "Bill"), new("supplier", "Supplier"), new("item", "Item"), new("qty", "Qty", ColType.Qty, true),
                new("rate", "Rate", ColType.Money), new("cost", "Landed Cost/Unit", ColType.Money), new("mrp", "MRP", ColType.Money),
                new("margin", "Margin vs MRP %", ColType.Percent), new("value", "Cost Value", ColType.Money, true)],
        };
        foreach (var l in lines)
        {
            decimal mrpBase = l.UnitFactor == 0 ? 0 : l.Mrp / l.UnitFactor;
            r.Rows.Add(Row(("date", data.D(l.Date)), ("no", l.DocNo), ("supplier", l.PartyName), ("item", l.ProductName), ("qty", l.BaseQty),
                ("rate", l.Rate), ("cost", l.Cost), ("mrp", mrpBase), ("margin", l.Cost == 0 ? 0 : Math.Round((mrpBase - l.Cost) * 100 / l.Cost, 2)),
                ("value", Math.Round(l.CostTotal, 2))).Link($"document:{l.DocId}"));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> BillProfit(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var r = new ReportResult
        {
            Title = "Bill-wise Profit", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("date", "Date", ColType.Date), new("no", "Bill No"), new("party", "Party"), new("value", "Sale Value", ColType.Money, true),
                new("cost", "Cost", ColType.Money, true), new("profit", "Profit", ColType.Money, true), new("pct", "Profit %", ColType.Percent)],
        };
        foreach (var d in docs)
        {
            decimal value = d.TaxableAmount + d.ExemptAmount - d.CashDiscount;
            r.Rows.Add(Row(("date", data.D(d.Date)), ("no", d.DocumentNo), ("party", d.PartyName), ("value", value), ("cost", d.Cost), ("profit", d.Profit),
                ("pct", d.Cost == 0 ? 0 : Math.Round(d.Profit * 100 / d.Cost, 2))).Link($"document:{d.Id}"));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> ItemProfit(ReportFilter f, Func<LineRow, (long Id, string Name)> key, string title, string reportTitle, CancellationToken ct)
    {
        var lines = await data.LinesAsync(ReportData.SalesTypes, f, ct);
        var r = new ReportResult
        {
            Title = reportTitle, Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("name", title), new("qty", "Qty", ColType.Qty, true), new("value", "Sale Value", ColType.Money, true),
                new("cost", "Cost", ColType.Money, true), new("profit", "Profit", ColType.Money, true), new("pct", "Profit %", ColType.Percent)],
        };
        foreach (var g in lines.GroupBy(key).OrderByDescending(g => g.Sum(l => l.Profit)))
        {
            decimal cost = Math.Round(g.Sum(l => l.CostTotal), 2), profit = g.Sum(l => l.Profit);
            r.Rows.Add(Row(("name", g.Key.Name), ("qty", g.Sum(l => l.BaseQty)), ("value", g.Sum(l => l.Taxable)), ("cost", cost), ("profit", profit),
                ("pct", cost == 0 ? 0 : Math.Round(profit * 100 / cost, 2))));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Production(ReportFilter f, StockDirection dir, string title, CancellationToken ct)
    {
        var lines = (await data.LinesAsync([VoucherType.Production], f, ct)).Where(l => l.Direction == dir).ToList();
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("date", "Date", ColType.Date), new("no", "Production No"), new("item", "Item"), new("batch", "Batch"),
                new("qty", "Qty", ColType.Qty, true), new("cost", "Cost/Unit", ColType.Money), new("value", "Value", ColType.Money, true)],
        };
        foreach (var l in lines)
            r.Rows.Add(Row(("date", data.D(l.Date)), ("no", l.DocNo), ("item", l.ProductName), ("batch", l.Batch), ("qty", l.BaseQty), ("cost", l.Cost),
                ("value", Math.Round(l.CostTotal, 2))).Link($"document:{l.DocId}"));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> PriceList(ReportFilter f, CancellationToken ct)
    {
        var lists = await data.Db.PriceLists.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        var items = await data.Db.PriceListItems.AsNoTracking().ToListAsync(ct);
        var products = await data.Db.Products.AsNoTracking().Where(p => p.IsActive && (f.CategoryId == null || p.CategoryId == f.CategoryId)).OrderBy(p => p.Name).ToListAsync(ct);
        var cols = new List<ReportColumn> { new("code", "Code"), new("name", "Item"), new("unit", "Unit"), new("prate", "Purchase", ColType.Money),
            new("srate", "Sale", ColType.Money), new("mrp", "MRP", ColType.Money) };
        cols.AddRange(lists.Select(l => new ReportColumn($"pl{l.Id}", l.Name, ColType.Money)));
        var r = new ReportResult { Title = "Price List", Columns = cols, Landscape = lists.Count > 2 };
        foreach (var p in products)
        {
            var row = Row(("code", p.Code), ("name", p.Name), ("unit", p.MainUnit), ("prate", p.PurchaseRate), ("srate", p.SaleRate), ("mrp", p.Mrp));
            foreach (var l in lists) row[$"pl{l.Id}"] = items.FirstOrDefault(i => i.PriceListId == l.Id && i.ProductId == p.Id)?.Price;
            r.Rows.Add(row);
        }
        return r;
    }

    private async Task<ReportResult> Loyalty(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var q = data.Db.LoyaltyTransactions.AsNoTracking().Where(t => t.FinancialYearId == fy && t.Date < end);
        if (f.PartyId is not null) q = q.Where(t => t.LedgerId == f.PartyId);
        var rows = await q.GroupBy(t => t.LedgerId)
            .Select(g => new { g.Key, In = g.Sum(t => t.PointsIn), Out = g.Sum(t => t.PointsOut), Sales = g.Sum(t => t.SaleAmount), Bills = g.Count() })
            .ToListAsync(ct);
        var names = await data.Db.Ledgers.AsNoTracking().Where(l => rows.Select(x => x.Key).Contains(l.Id))
            .Select(l => new { l.Id, l.Name, l.Mobile, Scheme = l.LoyaltyScheme != null ? l.LoyaltyScheme.Name : null }).ToDictionaryAsync(x => x.Id, ct);
        var r = new ReportResult
        {
            Title = "Customer Loyalty Points", Subtitle = $"Up to {to:dd-MM-yyyy}",
            Columns = [new("customer", "Customer"), new("mobile", "Mobile"), new("scheme", "Scheme"), new("bills", "Bills", ColType.Number, true),
                new("sales", "Sales", ColType.Money, true), new("earned", "Earned", ColType.Number, true), new("redeemed", "Redeemed", ColType.Number, true),
                new("balance", "Balance", ColType.Number, true)],
        };
        foreach (var x in rows.OrderByDescending(x => x.In - x.Out))
        {
            var n = names.GetValueOrDefault(x.Key);
            r.Rows.Add(Row(("customer", n?.Name), ("mobile", n?.Mobile), ("scheme", n?.Scheme), ("bills", x.Bills), ("sales", x.Sales), ("earned", x.In),
                ("redeemed", x.Out), ("balance", x.In - x.Out)));
        }
        return r.ComputeTotals();
    }
}
