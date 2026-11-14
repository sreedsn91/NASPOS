using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using NasPos.Domain.Validation;
using static NasPos.Application.Reports.RowExt;

namespace NasPos.Application.Reports;

/// <summary>GST returns and tax analysis. Each report is a parameterised query over posted, non-cancelled documents.</summary>
public class TaxReports(ReportData data) : IReportProvider
{
    /// <summary>B2C inter-state invoices above this value go to B2CL (₹1 lakh since Aug 2024).</summary>
    public const decimal B2ClThreshold = 100000m;

    public IEnumerable<ReportDefinition> Definitions =>
    [
        new("gstr1-b2b", "GSTR-1: B2B Invoices (4A)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-b2cl", "GSTR-1: B2C Large (5A)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-b2cs", "GSTR-1: B2C Small (7)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-cdnr", "GSTR-1: Credit Notes Registered (9B)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-cdnur", "GSTR-1: Credit Notes Unregistered (9B)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-hsn", "GSTR-1: HSN Summary (12)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-docs", "GSTR-1: Documents Issued (13)", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("gstr1-kfcess", "Kerala Flood Cess Summary", "GSTR-1", Screens.TaxReports, [F.Dates]),
        new("hsn-sales", "HSN/SAC Summary — Sales", "Tax Analysis", Screens.TaxReports, [F.Dates, F.TaxType]),
        new("hsn-purchase", "HSN/SAC Summary — Purchase", "Tax Analysis", Screens.TaxReports, [F.Dates, F.TaxType]),
        new("taxsplit-sales", "Tax Split by Rate — Sales", "Tax Analysis", Screens.TaxReports, [F.Dates, F.TaxType, F.VoucherType]),
        new("taxsplit-purchase", "Tax Split by Rate — Purchase", "Tax Analysis", Screens.TaxReports, [F.Dates, F.TaxType]),
        new("tax-daily", "Daily Tax Summary", "Tax Analysis", Screens.TaxReports, [F.Dates]),
        new("tax-summary", "Output vs Input Tax", "Tax Analysis", Screens.TaxReports, [F.Dates]),
        new("gst-purchase-register", "Purchase GST Register (Inward B2B)", "Tax Analysis", Screens.TaxReports, [F.Dates, F.Supplier]),
    ];

    public async Task<ReportResult> RunAsync(string key, ReportFilter f, CancellationToken ct)
    {
        var r = key switch
        {
            "gstr1-b2b" => await B2B(f, ct),
            "gstr1-b2cl" => await B2CL(f, ct),
            "gstr1-b2cs" => await B2CS(f, ct),
            "gstr1-cdnr" => await CreditNotes(f, registered: true, ct),
            "gstr1-cdnur" => await CreditNotes(f, registered: false, ct),
            "gstr1-hsn" => await Hsn(ReportData.SalesTypes, "GSTR-1: HSN Summary", f, ct, subtractReturns: true),
            "gstr1-docs" => await Docs(f, ct),
            "gstr1-kfcess" => await KfCess(f, ct),
            "hsn-sales" => await Hsn(ReportData.SalesTypes, "HSN/SAC Summary — Sales", f, ct, subtractReturns: false),
            "hsn-purchase" => await Hsn(ReportData.PurchaseTypes, "HSN/SAC Summary — Purchase", f, ct, subtractReturns: false),
            "taxsplit-sales" => await Split(ReportData.SalesTypes, "Tax Split by Rate — Sales", f, ct),
            "taxsplit-purchase" => await Split(ReportData.PurchaseTypes, "Tax Split by Rate — Purchase", f, ct),
            "tax-daily" => await Daily(f, ct),
            "tax-summary" => await Summary(f, ct),
            "gst-purchase-register" => await PurchaseRegister(f, ct),
            _ => throw new NotFoundException($"Unknown report {key}."),
        };
        r.Key = key;
        return r;
    }

    private static string Pos(LineRow l) => IndianStates.PlaceOfSupply(l.PartyState);

    private async Task<ReportResult> B2B(ReportFilter f, CancellationToken ct)
    {
        var lines = (await data.LinesAsync(ReportData.SalesTypes, f, ct)).Where(l => !string.IsNullOrEmpty(l.PartyGstin)).ToList();
        var docs = (await data.DocsAsync(ReportData.SalesTypes, f, false, ct)).ToDictionary(d => d.Id);
        var r = new ReportResult
        {
            Title = "GSTR-1: B2B Invoices", Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("gstin", "GSTIN/UIN of Recipient"), new("name", "Receiver Name"), new("no", "Invoice Number"), new("date", "Invoice Date", ColType.Date),
                new("value", "Invoice Value", ColType.Money), new("pos", "Place Of Supply"), new("rc", "Reverse Charge"), new("type", "Invoice Type"),
                new("rate", "Rate", ColType.Percent), new("taxable", "Taxable Value", ColType.Money, true), new("igst", "IGST", ColType.Money, true),
                new("cgst", "CGST", ColType.Money, true), new("sgst", "SGST", ColType.Money, true), new("cess", "Cess Amount", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => (l.DocId, l.GstRate)).OrderBy(g => g.First().Date))
        {
            var l = g.First();
            r.Rows.Add(Row(("gstin", l.PartyGstin), ("name", l.PartyName), ("no", l.DocNo), ("date", data.D(l.Date)), ("value", docs[l.DocId].BillAmount),
                ("pos", Pos(l)), ("rc", "N"), ("type", "Regular B2B"), ("rate", g.Key.GstRate), ("taxable", g.Sum(x => x.Taxable)), ("igst", g.Sum(x => x.Igst)),
                ("cgst", g.Sum(x => x.Cgst)), ("sgst", g.Sum(x => x.Sgst)), ("cess", g.Sum(x => x.Cess + x.AddCess))).Link($"document:{l.DocId}"));
        }
        r.ComputeTotals();
        r.Summary.Add(new("Invoices", lines.Select(l => l.DocId).Distinct().Count().ToString()));
        r.Summary.Add(new("Recipients", lines.Select(l => l.PartyGstin).Distinct().Count().ToString()));
        return r;
    }

    private async Task<ReportResult> B2CL(ReportFilter f, CancellationToken ct)
    {
        var docs = (await data.DocsAsync(ReportData.SalesTypes, f, false, ct))
            .Where(d => string.IsNullOrEmpty(d.PartyGstin) && d.TaxType == TaxType.InterState && d.BillAmount > B2ClThreshold).ToDictionary(d => d.Id);
        var lines = (await data.LinesAsync(ReportData.SalesTypes, f, ct)).Where(l => docs.ContainsKey(l.DocId)).ToList();
        var r = new ReportResult
        {
            Title = "GSTR-1: B2C Large", Subtitle = $"Inter-state B2C invoices above ₹{B2ClThreshold:#,0} · " + await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("no", "Invoice Number"), new("date", "Invoice Date", ColType.Date), new("value", "Invoice Value", ColType.Money), new("pos", "Place Of Supply"),
                new("rate", "Rate", ColType.Percent), new("taxable", "Taxable Value", ColType.Money, true), new("igst", "IGST", ColType.Money, true),
                new("cess", "Cess Amount", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => (l.DocId, l.GstRate)))
        {
            var l = g.First();
            r.Rows.Add(Row(("no", l.DocNo), ("date", data.D(l.Date)), ("value", docs[l.DocId].BillAmount), ("pos", Pos(l)), ("rate", g.Key.GstRate),
                ("taxable", g.Sum(x => x.Taxable)), ("igst", g.Sum(x => x.Igst)), ("cess", g.Sum(x => x.Cess + x.AddCess))).Link($"document:{l.DocId}"));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> B2CS(ReportFilter f, CancellationToken ct)
    {
        var docs = (await data.DocsAsync(ReportData.SalesTypes, f, false, ct))
            .Where(d => string.IsNullOrEmpty(d.PartyGstin) && !(d.TaxType == TaxType.InterState && d.BillAmount > B2ClThreshold)).Select(d => d.Id).ToHashSet();
        var lines = (await data.LinesAsync(ReportData.SalesTypes, f, ct)).Where(l => docs.Contains(l.DocId)).ToList();
        // B2C returns (unregistered) reduce B2CS.
        var returns = (await data.LinesAsync([VoucherType.SalesReturn], f, ct)).Where(l => string.IsNullOrEmpty(l.PartyGstin)).ToList();
        var company = await data.Db.Companies.AsNoTracking().FirstAsync(c => c.Id == data.CompanyId, ct);
        string Place(LineRow l) => IndianStates.PlaceOfSupply(string.IsNullOrEmpty(l.PartyState) ? company.StateCode : l.PartyState);
        var r = new ReportResult
        {
            Title = "GSTR-1: B2C Small", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("type", "Type"), new("pos", "Place Of Supply"), new("rate", "Rate", ColType.Percent), new("taxable", "Taxable Value", ColType.Money, true),
                new("igst", "IGST", ColType.Money, true), new("cgst", "CGST", ColType.Money, true), new("sgst", "SGST", ColType.Money, true),
                new("cess", "Cess Amount", ColType.Money, true)],
        };
        var all = lines.Select(l => (l, s: 1m)).Concat(returns.Select(l => (l, s: -1m)));
        foreach (var g in all.GroupBy(x => (Pos: Place(x.l), x.l.GstRate)).OrderBy(g => g.Key.Pos).ThenBy(g => g.Key.GstRate))
            r.Rows.Add(Row(("type", "OE"), ("pos", g.Key.Pos), ("rate", g.Key.GstRate), ("taxable", g.Sum(x => x.s * x.l.Taxable)), ("igst", g.Sum(x => x.s * x.l.Igst)),
                ("cgst", g.Sum(x => x.s * x.l.Cgst)), ("sgst", g.Sum(x => x.s * x.l.Sgst)), ("cess", g.Sum(x => x.s * (x.l.Cess + x.l.AddCess)))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> CreditNotes(ReportFilter f, bool registered, CancellationToken ct)
    {
        var lines = (await data.LinesAsync([VoucherType.SalesReturn], f, ct))
            .Where(l => registered ? !string.IsNullOrEmpty(l.PartyGstin) : string.IsNullOrEmpty(l.PartyGstin) && l.TaxType == TaxType.InterState).ToList();
        var docs = (await data.DocsAsync([VoucherType.SalesReturn], f, false, ct)).ToDictionary(d => d.Id);
        var r = new ReportResult
        {
            Title = registered ? "GSTR-1: Credit Notes (Registered)" : "GSTR-1: Credit Notes (Unregistered)", Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("gstin", "GSTIN/UIN"), new("name", "Receiver Name"), new("no", "Note Number"), new("date", "Note Date", ColType.Date), new("ntype", "Note Type"),
                new("pos", "Place Of Supply"), new("value", "Note Value", ColType.Money), new("rate", "Rate", ColType.Percent),
                new("taxable", "Taxable Value", ColType.Money, true), new("igst", "IGST", ColType.Money, true), new("cgst", "CGST", ColType.Money, true),
                new("sgst", "SGST", ColType.Money, true), new("cess", "Cess Amount", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => (l.DocId, l.GstRate)))
        {
            var l = g.First();
            r.Rows.Add(Row(("gstin", l.PartyGstin), ("name", l.PartyName), ("no", l.DocNo), ("date", data.D(l.Date)), ("ntype", "C"), ("pos", Pos(l)),
                ("value", docs[l.DocId].BillAmount), ("rate", g.Key.GstRate), ("taxable", g.Sum(x => x.Taxable)), ("igst", g.Sum(x => x.Igst)),
                ("cgst", g.Sum(x => x.Cgst)), ("sgst", g.Sum(x => x.Sgst)), ("cess", g.Sum(x => x.Cess + x.AddCess))).Link($"document:{l.DocId}"));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Hsn(VoucherType[] types, string title, ReportFilter f, CancellationToken ct, bool subtractReturns)
    {
        var lines = (await data.LinesAsync(types, f, ct)).Select(l => (l, s: 1m)).ToList();
        if (subtractReturns) lines.AddRange((await data.LinesAsync([VoucherType.SalesReturn], f, ct)).Select(l => (l, s: -1m)));
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("hsn", "HSN"), new("desc", "Description"), new("uqc", "UQC"), new("qty", "Total Quantity", ColType.Qty, true), new("rate", "Rate", ColType.Percent),
                new("value", "Total Value", ColType.Money, true), new("taxable", "Taxable Value", ColType.Money, true), new("igst", "Integrated Tax", ColType.Money, true),
                new("cgst", "Central Tax", ColType.Money, true), new("sgst", "State/UT Tax", ColType.Money, true), new("cess", "Cess", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(x => (Hsn: string.IsNullOrWhiteSpace(x.l.Hsn) ? "(blank)" : x.l.Hsn.Trim(), x.l.GstRate)).OrderBy(g => g.Key.Hsn))
            r.Rows.Add(Row(("hsn", g.Key.Hsn), ("desc", g.First().l.CategoryName ?? g.First().l.ProductName), ("uqc", Uqc(g.First().l.Unit)),
                ("qty", g.Sum(x => x.s * x.l.BaseQty)), ("rate", g.Key.GstRate), ("value", g.Sum(x => x.s * x.l.LineTotal)),
                ("taxable", g.Sum(x => x.s * x.l.Taxable)), ("igst", g.Sum(x => x.s * x.l.Igst)), ("cgst", g.Sum(x => x.s * x.l.Cgst)),
                ("sgst", g.Sum(x => x.s * x.l.Sgst)), ("cess", g.Sum(x => x.s * (x.l.Cess + x.l.AddCess)))));
        return r.ComputeTotals();
    }

    /// <summary>Maps common units to GST unit quantity codes.</summary>
    public static string Uqc(string? unit) => (unit ?? "").Trim().ToUpperInvariant() switch
    {
        "NOS" or "NO" or "PCS" or "PC" or "PIECE" => "NOS-NUMBERS",
        "KG" or "KGS" => "KGS-KILOGRAMS",
        "G" or "GM" or "GMS" => "GMS-GRAMMES",
        "L" or "LTR" or "LTRS" => "LTR-LITRES",
        "ML" => "MLT-MILILITRE",
        "BOX" => "BOX-BOX",
        "PKT" or "PACK" => "PAC-PACKS",
        "MTR" or "M" => "MTR-METERS",
        "SQFT" => "SQF-SQUARE FEET",
        "DOZ" or "DOZEN" => "DOZ-DOZENS",
        "BAG" => "BAG-BAGS",
        "SET" => "SET-SETS",
        "PAIR" => "PRS-PAIRS",
        "" => "OTH-OTHERS",
        var u => u,
    };

    private async Task<ReportResult> Docs(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync([.. ReportData.SalesTypes, VoucherType.SalesReturn], f, true, ct);
        var r = new ReportResult
        {
            Title = "GSTR-1: Documents Issued", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("nature", "Nature of Document"), new("from", "Sr. No. From"), new("to", "Sr. No. To"), new("total", "Total Number", ColType.Number, true),
                new("cancelled", "Cancelled", ColType.Number, true), new("net", "Net Issued", ColType.Number, true)],
        };
        foreach (var g in docs.GroupBy(d => d.VoucherType).OrderBy(g => g.Key))
        {
            var ordered = g.OrderBy(d => d.SequenceNo).ToList();
            int cancelled = g.Count(d => d.Cancelled);
            r.Rows.Add(Row(("nature", g.Key == VoucherType.SalesReturn ? "Credit Note" : $"Invoices for outward supply ({g.Key.DisplayName()})"),
                ("from", ordered.First().DocumentNo), ("to", ordered.Last().DocumentNo), ("total", g.Count()), ("cancelled", cancelled), ("net", g.Count() - cancelled)));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> KfCess(ReportFilter f, CancellationToken ct)
    {
        var lines = (await data.LinesAsync(ReportData.SalesTypes, f, ct)).Where(l => l.KfCess != 0).ToList();
        var r = new ReportResult
        {
            Title = "Kerala Flood Cess Summary", Subtitle = await data.RangeTextAsync(f, ct),
            Columns = [new("rate", "KF Cess %", ColType.Percent), new("gst", "GST %", ColType.Percent), new("bills", "Bills", ColType.Number, true),
                new("taxable", "Taxable Value", ColType.Money, true), new("kf", "KF Cess", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => (l.KfCessRate, l.GstRate)).OrderBy(g => g.Key.GstRate))
            r.Rows.Add(Row(("rate", g.Key.KfCessRate), ("gst", g.Key.GstRate), ("bills", g.Select(l => l.DocId).Distinct().Count()),
                ("taxable", g.Sum(l => l.Taxable)), ("kf", g.Sum(l => l.KfCess))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Split(VoucherType[] types, string title, ReportFilter f, CancellationToken ct)
    {
        var lines = await data.LinesAsync(types, f, ct);
        var r = new ReportResult
        {
            Title = title, Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("rate", "GST Slab", ColType.Percent), new("taxable", "Taxable", ColType.Money, true), new("cgst", "CGST", ColType.Money, true),
                new("sgst", "SGST", ColType.Money, true), new("igst", "IGST", ColType.Money, true), new("cess", "Cess", ColType.Money, true),
                new("kf", "KF Cess", ColType.Money, true), new("tax", "Total Tax", ColType.Money, true), new("total", "Gross Total", ColType.Money, true)],
        };
        decimal[] slabs = [0, 0.25m, 1, 3, 5, 12, 18, 28, 40];
        var byRate = lines.GroupBy(l => l.GstRate).ToDictionary(g => g.Key);
        foreach (var rate in slabs.Union(byRate.Keys).Distinct().OrderBy(x => x))
        {
            var g = byRate.TryGetValue(rate, out var x) ? x.ToList() : [];
            if (g.Count == 0 && !slabs.Contains(rate)) continue;
            r.Rows.Add(Row(("rate", rate), ("taxable", g.Sum(l => l.Taxable)), ("cgst", g.Sum(l => l.Cgst)), ("sgst", g.Sum(l => l.Sgst)), ("igst", g.Sum(l => l.Igst)),
                ("cess", g.Sum(l => l.Cess + l.AddCess)), ("kf", g.Sum(l => l.KfCess)), ("tax", g.Sum(l => l.Tax)), ("total", g.Sum(l => l.LineTotal))));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Daily(ReportFilter f, CancellationToken ct)
    {
        var docs = await data.DocsAsync(ReportData.SalesTypes, f, false, ct);
        var r = new ReportResult
        {
            Title = "Daily Tax Summary", Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("date", "Date", ColType.Date), new("bills", "Bills", ColType.Number, true), new("taxable", "Taxable", ColType.Money, true),
                new("exempt", "Exempt", ColType.Money, true), new("cgst", "CGST", ColType.Money, true), new("sgst", "SGST", ColType.Money, true),
                new("igst", "IGST", ColType.Money, true), new("cess", "Cess", ColType.Money, true), new("kf", "KF Cess", ColType.Money, true),
                new("total", "Bill Total", ColType.Money, true)],
        };
        foreach (var g in docs.GroupBy(d => data.D(d.Date)).OrderBy(g => g.Key))
            r.Rows.Add(Row(("date", g.Key), ("bills", g.Count()), ("taxable", g.Sum(d => d.TaxableAmount)), ("exempt", g.Sum(d => d.ExemptAmount)),
                ("cgst", g.Sum(d => d.CgstAmount)), ("sgst", g.Sum(d => d.SgstAmount)), ("igst", g.Sum(d => d.IgstAmount)),
                ("cess", g.Sum(d => d.CessAmount + d.AddCessAmount)), ("kf", g.Sum(d => d.KfCessAmount)), ("total", g.Sum(d => d.BillAmount))));
        return r.ComputeTotals();
    }

    private async Task<ReportResult> Summary(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await data.RangeAsync(f, ct);
        var start = data.Clock.DayStart(from);
        var end = data.Clock.DayEnd(to);
        long fy = data.FinancialYearId;
        var codes = new[] { SystemLedger.CGSTOutput, SystemLedger.SGSTOutput, SystemLedger.IGSTOutput, SystemLedger.CessOutput, SystemLedger.KFCessOutput,
            SystemLedger.CGSTInput, SystemLedger.SGSTInput, SystemLedger.IGSTInput, SystemLedger.CessInput, SystemLedger.KFCessInput };
        var ledgers = await data.Db.Ledgers.AsNoTracking().Where(l => l.SystemCode != null && codes.Contains(l.SystemCode.Value))
            .ToDictionaryAsync(l => l.SystemCode!.Value, l => l.Id, ct);
        var ids = ledgers.Values.ToList();
        var sums = await data.Db.AccountEntries.AsNoTracking().Where(e => e.FinancialYearId == fy && ids.Contains(e.LedgerId) && e.Date >= start && e.Date < end)
            .GroupBy(e => e.LedgerId).Select(g => new { g.Key, Net = g.Sum(e => e.Credit - e.Debit) }).ToDictionaryAsync(x => x.Key, x => x.Net, ct);
        decimal Net(SystemLedger c) => ledgers.TryGetValue(c, out var id) ? sums.GetValueOrDefault(id) : 0;
        var r = new ReportResult
        {
            Title = "Output vs Input Tax", Subtitle = $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            Columns = [new("head", "Tax Head"), new("output", "Output (Collected)", ColType.Money, true), new("input", "Input (Paid)", ColType.Money, true),
                new("net", "Net Payable", ColType.Money, true)],
        };
        foreach (var (head, o, i) in new[] { ("CGST", SystemLedger.CGSTOutput, SystemLedger.CGSTInput), ("SGST", SystemLedger.SGSTOutput, SystemLedger.SGSTInput),
                     ("IGST", SystemLedger.IGSTOutput, SystemLedger.IGSTInput), ("Cess", SystemLedger.CessOutput, SystemLedger.CessInput),
                     ("KF Cess", SystemLedger.KFCessOutput, SystemLedger.KFCessInput) })
        {
            decimal output = Net(o), input = -Net(i);
            r.Rows.Add(Row(("head", head), ("output", output), ("input", input), ("net", output - input)));
        }
        return r.ComputeTotals();
    }

    private async Task<ReportResult> PurchaseRegister(ReportFilter f, CancellationToken ct)
    {
        var lines = await data.LinesAsync(ReportData.PurchaseTypes, f, ct);
        var docs = (await data.DocsAsync(ReportData.PurchaseTypes, f, false, ct)).ToDictionary(d => d.Id);
        var r = new ReportResult
        {
            Title = "Purchase GST Register", Subtitle = await data.RangeTextAsync(f, ct), Landscape = true,
            Columns = [new("gstin", "Supplier GSTIN"), new("name", "Supplier"), new("inv", "Supplier Inv No"), new("date", "Date", ColType.Date),
                new("value", "Invoice Value", ColType.Money), new("rate", "Rate", ColType.Percent), new("taxable", "Taxable", ColType.Money, true),
                new("igst", "IGST", ColType.Money, true), new("cgst", "CGST", ColType.Money, true), new("sgst", "SGST", ColType.Money, true),
                new("cess", "Cess", ColType.Money, true)],
        };
        foreach (var g in lines.GroupBy(l => (l.DocId, l.GstRate)).OrderBy(g => g.First().Date))
        {
            var l = g.First();
            var d = docs[l.DocId];
            r.Rows.Add(Row(("gstin", l.PartyGstin), ("name", l.PartyName), ("inv", d.ReferenceNo ?? d.DocumentNo), ("date", data.D(d.ReferenceDate is { } rd ? data.Clock.DayStart(rd) : d.Date)),
                ("value", d.BillAmount), ("rate", g.Key.GstRate), ("taxable", g.Sum(x => x.Taxable)), ("igst", g.Sum(x => x.Igst)), ("cgst", g.Sum(x => x.Cgst)),
                ("sgst", g.Sum(x => x.Sgst)), ("cess", g.Sum(x => x.Cess + x.AddCess))).Link($"document:{l.DocId}"));
        }
        return r.ComputeTotals();
    }
}
