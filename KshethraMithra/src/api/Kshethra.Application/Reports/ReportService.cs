using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kshethra.Application.Reports;

public class ReportService(IEnumerable<IReportProvider> providers, ICurrentUser user, IAppDbContext db, AuditService audit)
{
    public IEnumerable<ReportDefinition> Catalog() =>
        providers.SelectMany(p => p.Definitions).Where(d => user.HasRight(d.Screen, Right.Read));

    private (IReportProvider Provider, ReportDefinition Def) Find(string key)
    {
        foreach (var p in providers)
            if (p.Definitions.FirstOrDefault(d => d.Key == key) is { } def) return (p, def);
        throw new NotFoundException($"Unknown report '{key}'.");
    }

    public async Task<ReportResult> RunAsync(string key, ReportFilter filter, CancellationToken ct)
    {
        var (p, def) = Find(key);
        if (!user.HasRight(def.Screen, Right.Read)) throw new ForbiddenException("You do not have access to this report.");
        return await p.RunAsync(key, filter, ct);
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> ExportAsync(string key, ReportFilter filter, string format, CancellationToken ct)
    {
        var (_, def) = Find(key);
        bool pdf = format.Equals("pdf", StringComparison.OrdinalIgnoreCase);
        if (!user.HasRight(def.Screen, pdf ? Right.Print : Right.Export)) throw new ForbiddenException("You do not have export rights for this report.");
        var result = await RunAsync(key, filter, ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        audit.Log(def.Screen, pdf ? "Print" : "Export", result.Title);
        await db.SaveChangesAsync(ct);
        string file = $"{key}-{DateTime.Now:yyyyMMddHHmm}";
        return pdf
            ? (ReportPdf.Render(result, company.Name), "application/pdf", file + ".pdf")
            : (ReportExcel.Render([result], company.Name), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx");
    }

    /// <summary>All GSTR-1 sections in one workbook, one sheet per section.</summary>
    public async Task<(byte[] Content, string FileName)> Gstr1WorkbookAsync(ReportFilter filter, CancellationToken ct)
    {
        if (!user.HasRight(Screens.TaxReports, Right.Export)) throw new ForbiddenException("You do not have export rights for tax reports.");
        string[] keys = ["gstr1-b2b", "gstr1-b2cl", "gstr1-b2cs", "gstr1-cdnr", "gstr1-cdnur", "gstr1-hsn", "gstr1-docs"];
        var results = new List<ReportResult>();
        foreach (var k in keys) results.Add(await RunAsync(k, filter, ct));
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        audit.Log(Screens.TaxReports, "Export", "GSTR-1 workbook");
        await db.SaveChangesAsync(ct);
        return (ReportExcel.Render(results, company.Name, k => k.Replace("gstr1-", "").ToUpperInvariant()), $"GSTR1-{DateTime.Now:yyyyMMdd}.xlsx");
    }
}

public static class ReportExcel
{
    public static byte[] Render(IEnumerable<ReportResult> reports, string company, Func<string, string>? sheetName = null)
    {
        using var wb = new XLWorkbook();
        foreach (var r in reports)
        {
            var name = (sheetName?.Invoke(r.Key) ?? r.Title);
            name = new string(name.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            if (name.Length > 31) name = name[..31];
            if (wb.Worksheets.Any(w => w.Name == name)) name = name[..Math.Min(28, name.Length)] + wb.Worksheets.Count;
            var ws = wb.Worksheets.Add(name);
            ws.Cell(1, 1).Value = company;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(2, 1).Value = r.Title + (r.Subtitle is null ? "" : $" — {r.Subtitle}");
            int row = 4;
            for (int c = 0; c < r.Columns.Count; c++)
            {
                var cell = ws.Cell(row, c + 1);
                cell.Value = r.Columns[c].Title;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
            }
            foreach (var dataRow in r.Rows)
            {
                row++;
                dataRow.TryGetValue(ReportResult.StyleKey, out var style);
                for (int c = 0; c < r.Columns.Count; c++)
                {
                    var col = r.Columns[c];
                    var cell = ws.Cell(row, c + 1);
                    SetValue(cell, col, dataRow.GetValueOrDefault(col.Key));
                    if (style is "header" or "total" or "subtotal" or "title") cell.Style.Font.Bold = true;
                }
            }
            if (r.Totals is not null)
            {
                row++;
                for (int c = 0; c < r.Columns.Count; c++)
                {
                    var cell = ws.Cell(row, c + 1);
                    SetValue(cell, r.Columns[c], r.Totals.GetValueOrDefault(r.Columns[c].Key));
                    cell.Style.Font.Bold = true;
                    cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                }
            }
            foreach (var s in r.Summary) { row += 1; ws.Cell(row, 1).Value = s.Key; ws.Cell(row, 2).Value = s.Value; ws.Cell(row, 1).Style.Font.Bold = true; }
            ws.Columns().AdjustToContents(4, row, 8, 60);
            ws.SheetView.FreezeRows(4);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetValue(IXLCell cell, ReportColumn col, object? v)
    {
        if (v is null) return;
        switch (col.Type)
        {
            case ColType.Money or ColType.Qty or ColType.Number or ColType.Percent when v is IConvertible && decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d):
                cell.Value = d;
                cell.Style.NumberFormat.Format = col.Type switch { ColType.Money => "#,##0.00", ColType.Qty => "#,##0.###", ColType.Percent => "0.##", _ => "#,##0" };
                break;
            case ColType.Date when DateOnly.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var dt):
                cell.Value = dt.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "dd-mm-yyyy";
                break;
            default:
                cell.Value = Convert.ToString(v, CultureInfo.InvariantCulture);
                break;
        }
    }
}

public static class ReportPdf
{
    public static string Format(ReportColumn col, object? v)
    {
        if (v is null) return "";
        if (col.Type is ColType.Money or ColType.Qty or ColType.Number or ColType.Percent
            && decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return col.Type switch
            {
                ColType.Money => d.ToString("#,##0.00", CultureInfo.InvariantCulture),
                ColType.Qty => d.ToString("#,##0.###", CultureInfo.InvariantCulture),
                ColType.Percent => d.ToString("0.##", CultureInfo.InvariantCulture),
                _ => d.ToString("#,##0", CultureInfo.InvariantCulture),
            };
        if (col.Type == ColType.Date && DateOnly.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, out var dt))
            return dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
    }

    public static byte[] Render(ReportResult r, string company) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(r.Landscape || r.Columns.Count > 8 ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(20);
            page.DefaultTextStyle(t => t.FontSize(8));
            page.Header().Column(c =>
            {
                c.Item().Text(company).Bold().FontSize(12);
                c.Item().Text(r.Title).SemiBold().FontSize(10);
                if (r.Subtitle is not null) c.Item().Text(r.Subtitle).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingBottom(4);
            });
            page.Content().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    foreach (var col in r.Columns)
                        if (col.Type is ColType.Text) cd.RelativeColumn(2); else cd.RelativeColumn(1);
                });
                t.Header(h =>
                {
                    foreach (var col in r.Columns)
                        h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight(col.Type is not (ColType.Text or ColType.Date)).Text(col.Title).SemiBold();
                });
                foreach (var row in r.Rows)
                {
                    row.TryGetValue(ReportResult.StyleKey, out var style);
                    bool bold = style is "header" or "total" or "subtotal" or "title";
                    foreach (var col in r.Columns)
                    {
                        var cell = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(2);
                        if (style is "total") cell = cell.Background(Colors.Grey.Lighten4);
                        var text = cell.AlignRight(col.Type is not (ColType.Text or ColType.Date)).Text(Format(col, row.GetValueOrDefault(col.Key)));
                        if (bold) text.SemiBold();
                        if (style is "danger") text.FontColor(Colors.Red.Darken2);
                    }
                }
                if (r.Totals is not null)
                    foreach (var col in r.Columns)
                        t.Cell().BorderTop(1).Padding(2).AlignRight(col.Type is not (ColType.Text or ColType.Date)).Text(Format(col, r.Totals.GetValueOrDefault(col.Key))).Bold();
            });
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text(string.Join("   ", r.Summary.Select(s => $"{s.Key}: {s.Value}"))).SemiBold();
                row.ConstantItem(80).AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
            });
        })).GeneratePdf();
}

internal static class QuestExt
{
    public static IContainer AlignRight(this IContainer c, bool right) => right ? c.AlignRight() : c.AlignLeft();
}
