using Kshethra.Domain;

namespace Kshethra.Application.Reports;

public static class ColType
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Money = "money";
    public const string Qty = "qty";
    public const string Date = "date";
    public const string Percent = "percent";
}

public sealed record ReportColumn(string Key, string Title, string Type = ColType.Text, bool Total = false);

public sealed class ReportResult
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public List<ReportColumn> Columns { get; set; } = [];
    public List<Dictionary<string, object?>> Rows { get; set; } = [];
    public Dictionary<string, object?>? Totals { get; set; }
    /// <summary>Label/value pairs shown under the grid (e.g. net profit, difference).</summary>
    public List<KeyValuePair<string, string>> Summary { get; set; } = [];
    public bool Landscape { get; set; }

    /// <summary>Row style marker: "header", "subtotal", "total", "indent".</summary>
    public const string StyleKey = "_style";
    /// <summary>Drill-down target for the row, e.g. "ledger:12" or "document:34".</summary>
    public const string LinkKey = "_link";

    public ReportResult ComputeTotals()
    {
        var totalCols = Columns.Where(c => c.Total).ToList();
        if (totalCols.Count == 0) return this;
        Totals = new Dictionary<string, object?>();
        var dataRows = Rows.Where(r => !r.ContainsKey(StyleKey)).ToList();
        foreach (var c in totalCols)
            Totals[c.Key] = dataRows.Sum(r => r.TryGetValue(c.Key, out var v) && v is not null ? Convert.ToDecimal(v) : 0m);
        Totals[Columns[0].Key] = "Total";
        return this;
    }
}

public sealed class ReportFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public VoucherType? VoucherType { get; set; }
    public PaymentMode? PaymentMode { get; set; }
    public TaxType? TaxType { get; set; }
    public BillType? BillType { get; set; }
    public long? PartyId { get; set; }
    public long? LedgerId { get; set; }
    public long? GroupId { get; set; }
    public long? CategoryId { get; set; }
    public long? ProductId { get; set; }
    public long? ManufacturerId { get; set; }
    public long? SalespersonId { get; set; }
    public long? AreaId { get; set; }
    public ItemClass? ItemClass { get; set; }
    public string? Counter { get; set; }
    public long? UserId { get; set; }
    public int? Days { get; set; }
    public string? Search { get; set; }
    public bool Detailed { get; set; }
    /// <summary>"Receivable" / "Payable" for ageing reports.</summary>
    public string? Side { get; set; }
}

/// <summary>Filters a report launcher shows for a report.</summary>
public static class F
{
    public const string Dates = "dates";
    public const string AsOn = "asOn";
    public const string VoucherType = "voucherType";
    public const string PaymentMode = "paymentMode";
    public const string TaxType = "taxType";
    public const string Party = "party";
    public const string Customer = "customer";
    public const string Supplier = "supplier";
    public const string Ledger = "ledger";
    public const string Group = "group";
    public const string Category = "category";
    public const string Product = "product";
    public const string Manufacturer = "manufacturer";
    public const string Salesperson = "salesperson";
    public const string Area = "area";
    public const string ItemClass = "itemClass";
    public const string Counter = "counter";
    public const string User = "user";
    public const string Days = "days";
    public const string Detailed = "detailed";
    public const string Side = "side";
}

public sealed record ReportDefinition(string Key, string Title, string Group, string Screen, string[] Filters, string? Description = null);

public interface IReportProvider
{
    IEnumerable<ReportDefinition> Definitions { get; }
    Task<ReportResult> RunAsync(string key, ReportFilter filter, CancellationToken ct);
}

public static class RowExt
{
    public static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var d = new Dictionary<string, object?>(cells.Length);
        foreach (var (k, v) in cells) d[k] = v;
        return d;
    }

    public static Dictionary<string, object?> Style(this Dictionary<string, object?> row, string style)
    {
        row[ReportResult.StyleKey] = style;
        return row;
    }

    public static Dictionary<string, object?> Link(this Dictionary<string, object?> row, string link)
    {
        row[ReportResult.LinkKey] = link;
        return row;
    }
}
