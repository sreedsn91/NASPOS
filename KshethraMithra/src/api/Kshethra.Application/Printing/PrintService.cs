using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Accounting;
using Kshethra.Application.Common;
using Kshethra.Application.Documents;
using Kshethra.Application.Setup;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Validation;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Kshethra.Application.Printing;

public sealed record PrintSchemeDto(long Id, VoucherType VoucherType, string Name, PrintFormat Format, bool IsDefault, bool TextMode, PrintTemplate Template);
public sealed record LabelRequest(long? TemplateId, List<LabelItem> Items);
public sealed record LabelItem(long ProductId, long? BatchId, int Copies);
public sealed record LabelTemplateDto(long Id, string Name, decimal WidthMm, decimal HeightMm, int Columns, decimal GapMm, bool IsDefault, LabelFields Fields);

public class PrintService(IAppDbContext db, ICurrentUser user, IClock clock, DocumentService documents, VoucherService vouchers, AuditService audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string M(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Q(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public async Task<List<PrintSchemeDto>> SchemesAsync(VoucherType? type, CancellationToken ct)
    {
        var q = db.PrintSchemes.AsNoTracking();
        if (type is not null) q = q.Where(s => s.VoucherType == type);
        var rows = await q.OrderBy(s => s.VoucherType).ThenByDescending(s => s.IsDefault).ThenBy(s => s.Name).ToListAsync(ct);
        return rows.Select(s => new PrintSchemeDto(s.Id, s.VoucherType, s.Name, s.Format, s.IsDefault, s.TextMode,
            JsonSerializer.Deserialize<PrintTemplate>(s.TemplateJson, Json) ?? new PrintTemplate())).ToList();
    }

    public async Task<long> SaveSchemeAsync(PrintSchemeDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new DomainException("Scheme name is required.");
        var s = dto.Id > 0 ? await db.PrintSchemes.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Scheme not found.") : new PrintScheme();
        if (await db.PrintSchemes.AnyAsync(x => x.VoucherType == dto.VoucherType && x.Name == dto.Name.Trim() && x.Id != dto.Id, ct))
            throw new DomainException("A scheme with this name exists for the voucher type.");
        s.VoucherType = dto.VoucherType; s.Name = dto.Name.Trim(); s.Format = dto.Format; s.TextMode = dto.TextMode;
        s.TemplateJson = JsonSerializer.Serialize(dto.Template, Json);
        if (dto.IsDefault)
            await db.PrintSchemes.Where(x => x.VoucherType == dto.VoucherType && x.Id != dto.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false), ct);
        s.IsDefault = dto.IsDefault;
        if (dto.Id == 0) db.PrintSchemes.Add(s);
        audit.Log(Screens.Printing, dto.Id == 0 ? "Add" : "Edit", $"Print scheme {s.Name}");
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    public async Task DeleteSchemeAsync(long id, CancellationToken ct)
    {
        var s = await db.PrintSchemes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Scheme not found.");
        if (s.IsDefault) throw new DomainException("Make another scheme the default first.");
        db.PrintSchemes.Remove(s);
        await db.SaveChangesAsync(ct);
    }

    private async Task<(PrintScheme Scheme, PrintTemplate Template)> SchemeForAsync(VoucherType type, long? schemeId, CancellationToken ct)
    {
        var s = schemeId is not null
            ? await db.PrintSchemes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == schemeId, ct)
            : await db.PrintSchemes.AsNoTracking().Where(x => x.VoucherType == type).OrderByDescending(x => x.IsDefault).FirstOrDefaultAsync(ct);
        s ??= new PrintScheme { VoucherType = type, Format = PrintFormat.A4, TemplateJson = JsonSerializer.Serialize(PrintTemplate.Default(type), Json) };
        return (s, JsonSerializer.Deserialize<PrintTemplate>(s.TemplateJson, Json) ?? PrintTemplate.Default(type));
    }

    /// <summary>Copy marks printed under the title; GST practice is "Original for recipient" plus a marked duplicate.</summary>
    public static readonly IReadOnlyDictionary<string, string> CopyLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["original"] = "ORIGINAL FOR RECIPIENT",
        ["duplicate"] = "DUPLICATE COPY",
    };

    /// <summary>
    /// Bill PDF. With <paramref name="copies"/> (e.g. original, duplicate) each copy is printed in turn, marked under the title;
    /// without it a single unmarked copy is printed, as before.
    /// </summary>
    public async Task<byte[]> DocumentPdfAsync(long documentId, long? schemeId, CancellationToken ct, IReadOnlyList<string>? copies = null)
    {
        var labels = (copies ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (labels.Count > CopyLabels.Count || labels.Any(c => !CopyLabels.ContainsKey(c)))
            throw new DomainException($"Copies must be among: {string.Join(", ", CopyLabels.Keys)}.", "print_copies");
        var marks = labels.Count == 0 ? new List<string?> { null } : labels.Select(c => (string?)CopyLabels[c]).ToList();

        var doc = await documents.GetAsync(documentId, ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        var (scheme, template) = await SchemeForAsync(doc.VoucherType, schemeId, ct);
        audit.Log(Screens.ForVoucher(doc.VoucherType), "Print",
            $"{doc.VoucherType.DisplayName()} {doc.DocumentNo}" + (labels.Count > 0 ? $" ({string.Join(" + ", labels)})" : ""));
        await db.SaveChangesAsync(ct);
        bool thermal = ThermalPaper.Is(scheme.Format);
        return Document.Create(document =>
        {
            foreach (var mark in marks)
            {
                if (thermal) ThermalPage(document, doc, company, template, scheme.Format, mark);
                else FullPage(document, doc, company, template, scheme.Format == PrintFormat.A5 ? PageSizes.A5 : PageSizes.A4, mark);
            }
        }).GeneratePdf();
    }

    private string DocDate(DateTimeOffset d) => d.ToOffset(clock.Offset).ToString("dd-MM-yyyy hh:mm tt", CultureInfo.InvariantCulture);

    private static IEnumerable<(decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Cess)> TaxSummary(TradeDocumentDto d) =>
        d.Lines.GroupBy(l => l.CgstRate + l.SgstRate + l.IgstRate).OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.CgstAmount), g.Sum(l => l.SgstAmount), g.Sum(l => l.IgstAmount), g.Sum(l => l.CessAmount + l.AddCessAmount + l.KfCessAmount)));

    private void FullPage(IDocumentContainer document, TradeDocumentDto d, Company c, PrintTemplate t, PageSize size, string? copyMark) =>
        document.Page(page =>
        {
            page.Size(size);
            page.Margin(size == PageSizes.A5 ? 14 : 24);
            page.DefaultTextStyle(x => x.FontSize(size == PageSizes.A5 ? 7.5f : 8.5f));
            page.Header().Column(col =>
            {
                col.Item().Row(row =>
                {
                    if (t.ShowLogo && c.Logo is { Length: > 0 }) row.ConstantItem(60).Height(50).Image(c.Logo).FitArea();
                    row.RelativeItem().Column(h =>
                    {
                        h.Item().Text(c.LegalName ?? c.Name).Bold().FontSize(14);
                        if (t.ShowCompanyAddress)
                            h.Item().Text(string.Join(", ", new[] { c.Address1, c.Address2, c.City, c.State, c.PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x))));
                        var contact = string.Join("  ", new[] { c.Phone is null ? null : $"Ph: {c.Phone}", c.Email }.Where(x => x is not null));
                        if (contact.Length > 0) h.Item().Text(contact);
                        if (t.ShowGstin && c.Gstin is not null) h.Item().Text($"GSTIN: {c.Gstin}   State: {IndianStates.PlaceOfSupply(c.StateCode)}").SemiBold();
                        foreach (var line in t.HeaderLines) h.Item().Text(line);
                    });
                    row.ConstantItem(150).AlignRight().Column(h =>
                    {
                        h.Item().AlignRight().Text(d.Cancelled ? $"{t.Title} (CANCELLED)" : t.Title).Bold().FontSize(12);
                        if (copyMark is not null) h.Item().AlignRight().Text(copyMark).SemiBold().FontSize(7).FontColor(Colors.Grey.Darken2);
                        h.Item().AlignRight().Text($"No: {d.DocumentNo}").SemiBold();
                        h.Item().AlignRight().Text($"Date: {DocDate(d.Date)}");
                        if (!string.IsNullOrEmpty(d.ReferenceNo)) h.Item().AlignRight().Text($"Ref: {d.ReferenceNo}");
                        h.Item().AlignRight().Text($"{d.PaymentMode}");
                    });
                });
                if (t.ShowParty && !string.IsNullOrWhiteSpace(d.PartyName))
                    col.Item().PaddingTop(6).Border(0.5f).Padding(5).Row(r =>
                    {
                        r.RelativeItem().Column(p =>
                        {
                            p.Item().Text(d.VoucherType.IsPurchaseSide() ? "Supplier" : "Bill To").FontColor(Colors.Grey.Darken1);
                            p.Item().Text(d.PartyName).SemiBold();
                            if (!string.IsNullOrWhiteSpace(d.PartyAddress)) p.Item().Text(d.PartyAddress);
                            if (!string.IsNullOrWhiteSpace(d.PartyPhone)) p.Item().Text($"Ph: {d.PartyPhone}");
                        });
                        r.RelativeItem().AlignRight().Column(p =>
                        {
                            if (!string.IsNullOrWhiteSpace(d.PartyGstin)) p.Item().AlignRight().Text($"GSTIN: {d.PartyGstin}");
                            p.Item().AlignRight().Text($"Place of supply: {IndianStates.PlaceOfSupply(d.PartyStateCode ?? c.StateCode)}");
                            if (d.SalespersonName is not null) p.Item().AlignRight().Text($"Salesperson: {d.SalespersonName}");
                        });
                    });
            });

            page.Content().PaddingTop(6).Column(body =>
            {
                var cols = t.Columns.Where(x => x != "Gst" || d.Lines.Any(l => l.CgstRate + l.SgstRate + l.IgstRate > 0)).ToList();
                body.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        foreach (var col in cols)
                            if (col == "Item") cd.RelativeColumn(4); else if (col == "SlNo") cd.ConstantColumn(22); else cd.RelativeColumn(1.2f);
                    });
                    table.Header(h =>
                    {
                        foreach (var col in cols)
                            h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(ColumnTitle(col)).SemiBold();
                    });
                    int n = 1;
                    foreach (var l in d.Lines)
                    {
                        foreach (var col in cols)
                        {
                            var cell = table.Cell().BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten2).Padding(2);
                            string v = col switch
                            {
                                "SlNo" => (n).ToString(CultureInfo.InvariantCulture),
                                "Item" => l.ProductName + (t.ShowRegionalName && !string.IsNullOrWhiteSpace(l.RegionalName) ? $"\n{l.RegionalName}" : "")
                                          + (string.IsNullOrWhiteSpace(l.BatchCode) || l.BatchCode == l.ProductCode ? "" : $"  [{l.BatchCode}]"),
                                "Hsn" => l.HsnCode ?? "",
                                "Batch" => l.BatchCode ?? "",
                                "Qty" => Q(l.Qty) + (l.FreeQty > 0 ? $"+{Q(l.FreeQty)}" : "") + $" {l.Unit}",
                                "Mrp" => M(l.Mrp),
                                "Rate" => M(l.Rate),
                                "Disc" => l.DiscountAmount > 0 ? M(l.DiscountAmount) : "",
                                "Taxable" => M(l.Taxable),
                                "Gst" => Q(l.CgstRate + l.SgstRate + l.IgstRate) + "%",
                                "Cgst" => M(l.CgstAmount),
                                "Sgst" => M(l.SgstAmount),
                                "Igst" => M(l.IgstAmount),
                                "Total" => M(l.LineTotal),
                                _ => "",
                            };
                            if (col is "Item" or "Hsn" or "Batch" or "SlNo") cell.Text(v); else cell.AlignRight().Text(v);
                        }
                        n++;
                    }
                });

                body.Item().PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        if (t.ShowTaxSummary && d.Lines.Any(l => l.CgstRate + l.SgstRate + l.IgstRate > 0))
                            left.Item().Table(tt =>
                            {
                                tt.ColumnsDefinition(cd => { for (int i = 0; i < 6; i++) cd.RelativeColumn(); });
                                foreach (var h in new[] { "GST %", "Taxable", "CGST", "SGST", "IGST", "Cess" })
                                    tt.Cell().Background(Colors.Grey.Lighten4).Padding(2).Text(h).SemiBold();
                                foreach (var s in TaxSummary(d))
                                {
                                    tt.Cell().Padding(2).Text(Q(s.Rate));
                                    foreach (var v in new[] { s.Taxable, s.Cgst, s.Sgst, s.Igst, s.Cess }) tt.Cell().Padding(2).AlignRight().Text(M(v));
                                }
                            });
                        if (t.ShowAmountInWords) left.Item().PaddingTop(4).Text(AmountInWords.Rupees(d.BillAmount)).Italic();
                        if (t.ShowYouSaved && d.TotalMrp > d.BillAmount && d.VoucherType.IsSalesFamily())
                            left.Item().PaddingTop(2).Text($"You saved Rs. {M(d.TotalMrp - d.BillAmount)} on MRP").SemiBold();
                        if (!string.IsNullOrWhiteSpace(d.Remarks)) left.Item().PaddingTop(2).Text($"Remarks: {d.Remarks}");
                        if (d.PointsEarned > 0 || d.PointsRedeemed > 0) left.Item().Text($"Loyalty: earned {Q(d.PointsEarned)} pts, redeemed {Q(d.PointsRedeemed)} pts");
                    });
                    row.ConstantItem(190).Column(tot =>
                    {
                        void Line(string label, decimal v, bool bold = false)
                        {
                            if (v == 0 && !bold) return;
                            tot.Item().Row(r2 =>
                            {
                                var l1 = r2.RelativeItem().Text(label);
                                var l2 = r2.ConstantItem(80).AlignRight().Text(M(v));
                                if (bold) { l1.Bold().FontSize(11); l2.Bold().FontSize(11); }
                            });
                        }
                        Line("Gross", d.GrossAmount);
                        Line("Item discount", -d.ItemDiscount);
                        Line("Bill discount", -d.BillDiscount);
                        Line("Taxable", d.TaxableAmount + d.ExemptAmount);
                        Line("CGST", d.CgstAmount);
                        Line("SGST", d.SgstAmount);
                        Line("IGST", d.IgstAmount);
                        Line("Cess", d.CessAmount + d.AddCessAmount);
                        Line("KF cess", d.KfCessAmount);
                        Line("Service tax", d.ServiceTaxAmount + d.ServiceCessAmount);
                        Line("Other charges", d.OtherExpense + d.OtherExpenseTax);
                        Line("Freight", d.Freight + d.FreightTax);
                        Line("Round off", d.RoundOff);
                        Line("Cash discount", -d.CashDiscount);
                        Line("Loyalty redeemed", -d.RedeemAmount);
                        Line("NET AMOUNT", d.BillAmount, true);
                        if (d.PaymentMode == PaymentMode.Mixed)
                        {
                            Line("Cash", d.CashAmount); Line("Card", d.CardAmount); Line("Credit", d.CreditAmount);
                        }
                        if (d.TenderedAmount > 0) { Line("Tendered", d.TenderedAmount); Line("Balance", d.TenderedAmount - d.CashAmount); }
                    });
                });
            });

            page.Footer().Column(f =>
            {
                foreach (var line in t.FooterLines) f.Item().AlignCenter().Text(line);
                if (!string.IsNullOrWhiteSpace(c.InvoiceFooter)) f.Item().AlignCenter().Text(c.InvoiceFooter);
                f.Item().Row(r =>
                {
                    r.RelativeItem().Text("E. & O.E.").FontColor(Colors.Grey.Darken1);
                    if (t.ShowSignature) r.RelativeItem().AlignRight().Text($"For {c.Name}\n\n\nAuthorised Signatory");
                });
            });
        });

    private static string ColumnTitle(string col) => col switch
    {
        "SlNo" => "#", "Item" => "Item", "Hsn" => "HSN", "Batch" => "Batch", "Qty" => "Qty", "Mrp" => "MRP", "Rate" => "Rate",
        "Disc" => "Disc", "Taxable" => "Taxable", "Gst" => "GST", "Cgst" => "CGST", "Sgst" => "SGST", "Igst" => "IGST", "Total" => "Amount",
        _ => col,
    };

    private void ThermalPage(IDocumentContainer document, TradeDocumentDto d, Company c, PrintTemplate t, PrintFormat format, string? copyMark) =>
        document.Page(page =>
        {
            ThermalPaper.Apply(page, format);
            page.DefaultTextStyle(x => x.FontSize(ThermalPaper.Narrow(format) ? 6.5f : 7.5f));
            page.Content().Column(col =>
            {
                col.Item().AlignCenter().Text(c.Name).Bold().FontSize(10);
                if (t.ShowCompanyAddress) col.Item().AlignCenter().Text(string.Join(", ", new[] { c.Address1, c.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
                if (c.Phone is not null) col.Item().AlignCenter().Text($"Ph: {c.Phone}");
                if (t.ShowGstin && c.Gstin is not null) col.Item().AlignCenter().Text($"GSTIN: {c.Gstin}");
                foreach (var line in t.HeaderLines) col.Item().AlignCenter().Text(line);
                col.Item().AlignCenter().PaddingVertical(2).Text(d.Cancelled ? $"{t.Title} (CANCELLED)" : t.Title).SemiBold();
                if (copyMark is not null) col.Item().AlignCenter().Text($"*** {copyMark} ***").SemiBold();
                col.Item().Row(r => { r.RelativeItem().Text($"No: {d.DocumentNo}"); r.RelativeItem().AlignRight().Text(DocDate(d.Date)); });
                if (!string.IsNullOrWhiteSpace(d.PartyName)) col.Item().Text($"To: {d.PartyName}" + (d.PartyGstin is null ? "" : $" ({d.PartyGstin})"));
                col.Item().LineHorizontal(0.5f);
                col.Item().Row(r =>
                {
                    r.RelativeItem(3).Text("Item").SemiBold();
                    r.RelativeItem(1).AlignRight().Text("Qty").SemiBold();
                    r.RelativeItem(1.3f).AlignRight().Text("Rate").SemiBold();
                    r.RelativeItem(1.5f).AlignRight().Text("Amt").SemiBold();
                });
                col.Item().LineHorizontal(0.5f);
                foreach (var l in d.Lines)
                {
                    col.Item().Text(l.ProductName + (t.ShowRegionalName && l.RegionalName is not null ? $" / {l.RegionalName}" : ""));
                    col.Item().Row(r =>
                    {
                        r.RelativeItem(3).Text(l.DiscountAmount > 0 ? $"  disc {M(l.DiscountAmount)}" : "");
                        r.RelativeItem(1).AlignRight().Text(Q(l.Qty + l.FreeQty));
                        r.RelativeItem(1.3f).AlignRight().Text(M(l.Rate));
                        r.RelativeItem(1.5f).AlignRight().Text(M(l.LineTotal));
                    });
                }
                col.Item().LineHorizontal(0.5f);
                void Tot(string label, decimal v, bool bold = false)
                {
                    if (v == 0 && !bold) return;
                    col.Item().Row(r =>
                    {
                        var a = r.RelativeItem().Text(label); var b = r.ConstantItem(70).AlignRight().Text(M(v));
                        if (bold) { a.Bold().FontSize(10); b.Bold().FontSize(10); }
                    });
                }
                Tot("Items / Qty", d.Lines.Count);
                Tot("Taxable", d.TaxableAmount + d.ExemptAmount);
                if (t.ShowTaxSummary) { Tot("CGST", d.CgstAmount); Tot("SGST", d.SgstAmount); Tot("IGST", d.IgstAmount); Tot("Cess", d.CessAmount + d.AddCessAmount + d.KfCessAmount); }
                Tot("Discount", -(d.BillDiscount + d.CashDiscount + d.RedeemAmount));
                Tot("Round off", d.RoundOff);
                Tot("TOTAL", d.BillAmount, true);
                if (d.TenderedAmount > 0) { Tot("Tendered", d.TenderedAmount); Tot("Balance", d.TenderedAmount - d.CashAmount); }
                if (t.ShowYouSaved && d.TotalMrp > d.BillAmount) col.Item().AlignCenter().PaddingTop(2).Text($"You saved Rs. {M(d.TotalMrp - d.BillAmount)}").SemiBold();
                if (d.PointsEarned > 0) col.Item().AlignCenter().Text($"Points earned: {Q(d.PointsEarned)}");
                col.Item().PaddingTop(4).AlignCenter().Height(28).Svg(Code128.Svg(d.DocumentNo ?? "", 30, 1));
                foreach (var line in t.FooterLines) col.Item().AlignCenter().Text(line);
            });
        });

    /// <summary>Fixed-width text receipt for raw thermal printing (42 columns at 80 mm, 32 at 58 mm).</summary>
    public async Task<string> DocumentTextAsync(long documentId, int width, CancellationToken ct)
    {
        var d = await documents.GetAsync(documentId, ct);
        var c = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == user.CompanyId, ct);
        var (_, t) = await SchemeForAsync(d.VoucherType, null, ct);
        width = Math.Clamp(width, 24, 64);
        var sb = new StringBuilder();
        string Center(string s) => s.Length >= width ? s[..width] : s.PadLeft((width + s.Length) / 2).PadRight(width);
        string Lr(string l, string r) => l.Length + r.Length + 1 > width ? l[..Math.Max(0, width - r.Length - 1)] + " " + r : l + r.PadLeft(width - l.Length);
        string Rule() => new('-', width);
        sb.AppendLine(Center(c.Name));
        if (c.Address1 is not null) sb.AppendLine(Center(c.Address1));
        if (c.Gstin is not null) sb.AppendLine(Center("GSTIN: " + c.Gstin));
        sb.AppendLine(Center(t.Title));
        sb.AppendLine(Lr($"No: {d.DocumentNo}", d.Date.ToOffset(clock.Offset).ToString("dd-MM-yy HH:mm", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(d.PartyName)) sb.AppendLine(("To: " + d.PartyName).PadRight(width)[..width]);
        sb.AppendLine(Rule());
        foreach (var l in d.Lines)
        {
            sb.AppendLine(l.ProductName!.Length > width ? l.ProductName[..width] : l.ProductName);
            sb.AppendLine(Lr($"  {Q(l.Qty + l.FreeQty)} x {M(l.Rate)}", M(l.LineTotal)));
        }
        sb.AppendLine(Rule());
        sb.AppendLine(Lr("Taxable", M(d.TaxableAmount + d.ExemptAmount)));
        if (d.CgstAmount != 0) sb.AppendLine(Lr("CGST", M(d.CgstAmount)));
        if (d.SgstAmount != 0) sb.AppendLine(Lr("SGST", M(d.SgstAmount)));
        if (d.IgstAmount != 0) sb.AppendLine(Lr("IGST", M(d.IgstAmount)));
        if (d.RoundOff != 0) sb.AppendLine(Lr("Round off", M(d.RoundOff)));
        sb.AppendLine(Lr("TOTAL", M(d.BillAmount)));
        if (d.TotalMrp > d.BillAmount && t.ShowYouSaved) sb.AppendLine(Center($"You saved Rs. {M(d.TotalMrp - d.BillAmount)}"));
        foreach (var line in t.FooterLines) sb.AppendLine(Center(line));
        return sb.ToString();
    }

    public async Task<byte[]> VoucherPdfAsync(long voucherId, CancellationToken ct)
    {
        var v = await vouchers.GetAsync(voucherId, ct);
        var c = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == user.CompanyId, ct);
        audit.Log(Screens.Voucher, "Print", $"{v.VoucherType.DisplayName()} {v.DocumentNo}");
        await db.SaveChangesAsync(ct);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A5.Landscape());
            page.Margin(20);
            page.DefaultTextStyle(x => x.FontSize(9));
            page.Header().Column(h =>
            {
                h.Item().AlignCenter().Text(c.Name).Bold().FontSize(13);
                h.Item().AlignCenter().Text(string.Join(", ", new[] { c.Address1, c.City }.Where(x => x is not null)));
                h.Item().PaddingTop(4).AlignCenter().Text((v.Cancelled ? "CANCELLED " : "") + v.VoucherType.DisplayName().ToUpperInvariant() + " VOUCHER").SemiBold().FontSize(11);
                h.Item().Row(r => { r.RelativeItem().Text($"No: {v.DocumentNo}"); r.RelativeItem().AlignRight().Text($"Date: {v.Date.ToOffset(clock.Offset):dd-MM-yyyy}"); });
            });
            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(4); cd.RelativeColumn(1); cd.RelativeColumn(1.5f); });
                    t.Header(h => { h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Particulars").SemiBold(); h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text("Dr/Cr").SemiBold(); h.Cell().Background(Colors.Grey.Lighten3).Padding(3).AlignRight().Text("Amount").SemiBold(); });
                    if (v.HeaderLedgerName is not null)
                    {
                        t.Cell().Padding(3).Text(v.HeaderLedgerName).SemiBold(); t.Cell().Padding(3).Text(v.HeaderSide.ToString()); t.Cell().Padding(3).AlignRight().Text(M(v.Total));
                    }
                    foreach (var l in v.Lines)
                    {
                        t.Cell().Padding(3).Text(l.LedgerName + (string.IsNullOrWhiteSpace(l.Narration) ? "" : $"\n  {l.Narration}")
                                                 + (l.Allocations.Count > 0 ? $"\n  Against: {string.Join(", ", l.Allocations.Select(a => $"{a.DocumentNo} ({M(a.Amount)})"))}" : ""));
                        t.Cell().Padding(3).Text(l.Side.ToString());
                        t.Cell().Padding(3).AlignRight().Text(M(l.Amount));
                    }
                });
                col.Item().PaddingTop(6).Text(AmountInWords.Rupees(v.Total)).Italic();
                if (!string.IsNullOrWhiteSpace(v.ChequeNo)) col.Item().Text($"Cheque {v.ChequeNo} dated {v.ChequeDate:dd-MM-yyyy} {v.Bank}");
                if (!string.IsNullOrWhiteSpace(v.Narration)) col.Item().Text($"Narration: {v.Narration}");
            });
            page.Footer().Row(r => { r.RelativeItem().Text("Receiver's signature"); r.RelativeItem().AlignRight().Text("Authorised signatory"); });
        })).GeneratePdf();
    }

    public async Task<List<LabelTemplateDto>> LabelTemplatesAsync(CancellationToken ct)
    {
        var rows = await db.LabelTemplates.AsNoTracking().OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name).ToListAsync(ct);
        return rows.Select(x => new LabelTemplateDto(x.Id, x.Name, x.WidthMm, x.HeightMm, x.Columns, x.GapMm, x.IsDefault,
            JsonSerializer.Deserialize<LabelFields>(x.FieldsJson, Json) ?? new LabelFields())).ToList();
    }

    public async Task<long> SaveLabelTemplateAsync(LabelTemplateDto dto, CancellationToken ct)
    {
        if (dto.WidthMm <= 0 || dto.HeightMm <= 0 || dto.Columns < 1) throw new DomainException("Label size and columns must be positive.");
        var t = dto.Id > 0 ? await db.LabelTemplates.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Template not found.") : new LabelTemplate();
        t.Name = dto.Name.Trim(); t.WidthMm = dto.WidthMm; t.HeightMm = dto.HeightMm; t.Columns = dto.Columns; t.GapMm = dto.GapMm;
        t.FieldsJson = JsonSerializer.Serialize(dto.Fields, Json);
        if (dto.IsDefault) await db.LabelTemplates.Where(x => x.Id != dto.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDefault, false), ct);
        t.IsDefault = dto.IsDefault;
        if (dto.Id == 0) db.LabelTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return t.Id;
    }

    /// <summary>Barcode label sheet: one label per copy, laid out in columns at the template's size.</summary>
    public async Task<byte[]> LabelsPdfAsync(LabelRequest r, CancellationToken ct)
    {
        var templates = await LabelTemplatesAsync(ct);
        var tpl = templates.FirstOrDefault(x => x.Id == r.TemplateId) ?? templates.FirstOrDefault()
                  ?? new LabelTemplateDto(0, "Default", 50, 25, 2, 2, true, new LabelFields());
        var company = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == user.CompanyId, ct);
        var pids = r.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var bids = r.Items.Where(i => i.BatchId is not null).Select(i => i.BatchId!.Value).ToList();
        var batches = await db.Batches.AsNoTracking().Where(b => bids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var labels = new List<(string Name, string Code, decimal Mrp, decimal Rate, DateOnly? Expiry)>();
        foreach (var item in r.Items.Where(i => i.Copies > 0))
        {
            if (!products.TryGetValue(item.ProductId, out var p)) continue;
            var b = item.BatchId is not null ? batches.GetValueOrDefault(item.BatchId.Value) : null;
            string code = b?.Code ?? p.DefaultBarcode ?? p.Code;
            for (int i = 0; i < Math.Min(item.Copies, 1000); i++)
                labels.Add((p.Name, code, b?.Mrp ?? p.Mrp, b?.SaleRate > 0 ? b.SaleRate : p.SaleRate, b?.ExpiryDate));
        }
        if (labels.Count == 0) throw new DomainException("Nothing to print.");
        const float mm = 2.8346f;
        float w = (float)tpl.WidthMm * mm, h = (float)tpl.HeightMm * mm, gap = (float)tpl.GapMm * mm;
        var f = tpl.Fields;
        audit.Log(Screens.Printing, "Print", $"{labels.Count} labels");
        await db.SaveChangesAsync(ct);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(new PageSize(tpl.Columns * w + (tpl.Columns - 1) * gap + 8, h + 4));
            page.Margin(2);
            page.DefaultTextStyle(x => x.FontSize((float)f.FontSize));
            page.Content().Column(col =>
            {
                foreach (var chunk in labels.Chunk(tpl.Columns))
                    col.Item().Height(h).Row(row =>
                    {
                        row.Spacing(gap);
                        foreach (var l in chunk)
                            row.ConstantItem(w).Padding(2).Column(c =>
                            {
                                if (f.ShowCompany) c.Item().AlignCenter().Text(company.Name).Bold().FontSize((float)f.FontSize - 1).ClampLines(1);
                                if (f.ShowName) c.Item().AlignCenter().Text(l.Name).ClampLines(1);
                                if (f.ShowBarcode) c.Item().Height(h * 0.38f).AlignCenter().Svg(Code128.Svg(l.Code, 30, 1, false));
                                if (f.ShowBarcodeText) c.Item().AlignCenter().Text(l.Code).FontSize((float)f.FontSize - 1);
                                c.Item().Row(pr =>
                                {
                                    if (f.ShowMrp) pr.RelativeItem().Text($"MRP {M(l.Mrp)}").SemiBold();
                                    if (f.ShowSaleRate && l.Rate < l.Mrp) pr.RelativeItem().AlignRight().Text($"Rs {M(l.Rate)}").Bold();
                                });
                                if (f.ShowExpiry && l.Expiry is not null) c.Item().Text($"Exp {l.Expiry:MM/yy}");
                                if (f.ShowPackedDate) c.Item().Text($"Pkd {clock.Today():dd/MM/yy}");
                            });
                    });
            });
        })).GeneratePdf();
    }
}
