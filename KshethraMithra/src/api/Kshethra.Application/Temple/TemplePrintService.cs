using System.Globalization;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Kshethra.Application.Common;
using Kshethra.Application.Printing;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Temple;

/// <summary>
/// Temple prints: vazhipadu receipt, the priests' daily pooja list (A4) and the donation receipt. Receipt paper
/// (thermal 80 / 58 mm, A5 or A4) comes from the Temple settings unless the caller asks for one. Text uses "Nirmala UI" (shipped with Windows 10/11), which has Malayalam glyphs, or
/// "Noto Sans Malayalam" (installed by the setup) where Nirmala UI is missing.
/// </summary>
public class TemplePrintService(IAppDbContext db, ICurrentUser user, BookingService bookings, PoojaListService poojaList,
    DonationService donations, TempleCalendarService calendar, IClock clock, SettingsService settings, IRawPrinter rawPrinter)
{
    private static readonly string[] Fonts = ["Nirmala UI", "Noto Sans Malayalam", "Arial"];
    private static string M(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Q(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private Task<Company> CompanyAsync(CancellationToken ct) => db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);

    private static void Header(ColumnDescriptor col, Company c, string title, bool small)
    {
        col.Item().AlignCenter().Text(c.Name).Bold().FontSize(small ? 10 : 13);
        var addr = string.Join(", ", new[] { c.Address1, c.Address2, c.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (addr.Length > 0) col.Item().AlignCenter().Text(addr);
        if (!string.IsNullOrWhiteSpace(c.Phone)) col.Item().AlignCenter().Text($"Ph: {c.Phone}");
        col.Item().PaddingVertical(3).AlignCenter().Text(title).Bold().FontSize(small ? 9 : 11);
    }

    /// <summary>Thermal printers drop light grey, so secondary text is grey only on A5 / A4.</summary>
    private static string Muted(bool thermal) => thermal ? Colors.Black : Colors.Grey.Darken2;

    // ---------------- vazhipadu receipt ----------------

    /// <summary>
    /// The vazhipadu receipt: a summary page with every offering and the total, then (when <paramref name="slips"/> or the
    /// "separate slip per offering" setting is on) one slip page per offering for the priest. Receipt-printer drivers
    /// usually cut only at the end of a print job, so the counter prints the summary and each slip
    /// (<see cref="BookingSlipAsync"/>) as separate jobs, or the server prints them straight to the receipt printer with a
    /// cut after each (<see cref="PrintBookingDirectAsync"/>); this combined PDF is for viewing and A5 / A4 printing.
    /// <paramref name="duplicates"/> extra copies of the receipt (marked DUPLICATE) follow it; null takes the setting.
    /// </summary>
    public async Task<byte[]> BookingReceiptAsync(long bookingId, PrintFormat? paper, bool? slips, int? duplicates, CancellationToken ct) =>
        (await BookingDocumentAsync(bookingId, paper, slips, duplicates, null, false, ct)).GeneratePdf();

    /// <summary>One offering's slip (line number from 1) on its own, to print as a separate, separately cut job.</summary>
    public async Task<byte[]> BookingSlipAsync(long bookingId, int lineNo, PrintFormat? paper, CancellationToken ct) =>
        (await BookingDocumentAsync(bookingId, paper, true, 0, lineNo, false, ct)).GeneratePdf();

    /// <summary>
    /// Prints the receipt, its duplicates and the slips (each per the arguments, or the settings when null) straight to the
    /// receipt printer named in the Temple settings, as one RAW ESC/POS job with a full cut after every piece. Each page is
    /// sent as a 203 dpi picture, so the layout and Malayalam match the PDF exactly and no browser dialog is involved.
    /// </summary>
    public async Task PrintBookingDirectAsync(long bookingId, bool? slips, int? duplicates, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (s.ReceiptPrinter.Length == 0)
            throw new DomainException("No receipt printer is set. Enter it in Temple › Vazhipadu Print Settings.", "no_receipt_printer");
        var paper = ThermalPaper.Is(s.VazhipaduReceiptPaper) ? s.VazhipaduReceiptPaper : PrintFormat.Thermal80;
        var doc = await BookingDocumentAsync(bookingId, paper, slips, duplicates, null, true, ct);
        var pages = doc.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = ThermalDpi });
        var b = await bookings.GetAsync(bookingId, ct);
        rawPrinter.Send(s.ReceiptPrinter, $"Vazhipadu {b.BookingNo}", EscPosRaster.Job(pages));
    }

    /// <summary>Thermal receipt printers print 8 dots per mm: 576 dots across a 72 mm page.</summary>
    private const int ThermalDpi = 203;

    /// <summary>Most duplicate receipt copies one print may ask for.</summary>
    public const int MaxDuplicates = 9;

    private async Task<IDocument> BookingDocumentAsync(long bookingId, PrintFormat? paper, bool? slips, int? duplicateCopies, int? onlyLine,
        bool continuous, CancellationToken ct)
    {
        var b = await bookings.GetAsync(bookingId, ct);
        if (onlyLine is { } ol && (ol < 1 || ol > b.Lines.Count)) throw new NotFoundException("Offering line not found.");
        var c = await CompanyAsync(ct);
        var today = await calendar.DayAsync(DateOnly.FromDateTime(b.BookedAt.ToOffset(clock.Offset).Date), ct);
        var s = await settings.GetAsync(ct);
        var duplicates = Math.Clamp(duplicateCopies ?? s.VazhipaduDuplicates, 0, MaxDuplicates);
        var format = paper ?? s.VazhipaduReceiptPaper;
        var withSlips = onlyLine is not null || ((slips ?? s.VazhipaduItemSlips) && b.Status != "Cancelled");
        var thermal = ThermalPaper.Is(format);
        var bookedAt = b.BookedAt.ToOffset(clock.Offset).ToString("dd-MM-yyyy HH:mm");

        void Setup(PageDescriptor page)
        {
            if (thermal) ThermalPaper.Apply(page, format, continuous);
            else { page.Size(format == PrintFormat.A4 ? PageSizes.A4 : PageSizes.A5); page.Margin(format == PrintFormat.A4 ? 28 : 18); }
            page.DefaultTextStyle(x => x.FontFamily(Fonts).FontSize(format == PrintFormat.Thermal58 ? 6.5f : thermal ? 7.5f : 9));
        }

        return Document.Create(doc =>
        {
            // The receipt, then its duplicate copies (marked), then the cut slips.
            if (onlyLine is null) for (int copy = 0; copy <= duplicates; copy++)
            {
            var duplicate = copy > 0;
            doc.Page(page =>
        {
            Setup(page);
            page.Content().Column(col =>
            {
                Header(col, c, "VAZHIPADU RECEIPT · വഴിപാട് രസീത്", thermal);
                if (duplicate) col.Item().AlignCenter().Text("*** DUPLICATE ***").Bold();
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text($"No: {b.BookingNo}").Bold();
                    r.RelativeItem().AlignRight().Text(bookedAt);
                });
                col.Item().Text($"{today.MalayalamDate} · {today.WeekdayMl}");
                col.Item().Text(t =>
                {
                    t.Span("Name: ");
                    t.Span(b.ContactName).Bold();
                    if (b.Phone is { Length: > 0 } p) t.Span($"  Ph: {p}");
                });
                if (b.Status == "Cancelled") col.Item().Text("CANCELLED").Bold().FontColor(Colors.Red.Darken2);
                col.Item().PaddingVertical(3).LineHorizontal(0.6f);
                foreach (var l in b.Lines)
                {
                    col.Item().PaddingBottom(4).Column(lc =>
                    {
                        lc.Item().Row(r =>
                        {
                            r.RelativeItem().Text(t =>
                            {
                                t.Span(l.Offering).Bold();
                                if (!string.IsNullOrWhiteSpace(l.OfferingMl)) t.Span($"  {l.OfferingMl}");
                            });
                            r.ConstantItem(thermal ? 55 : 70).AlignRight().Text(M(l.Amount)).Bold();
                        });
                        var who = Who(l);
                        if (who.Trim().Length > 0) lc.Item().Text(who).Bold();
                        if (l.Deity is { } d) lc.Item().Text($"Deity: {d}{(l.PoojaSlot is { } ps ? " · " + ps : "")}");
                        lc.Item().Text(l.Schedule).FontColor(Muted(thermal));
                        lc.Item().Text(QtyLine(l));
                    });
                }
                col.Item().PaddingVertical(3).LineHorizontal(0.6f);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text("TOTAL").Bold().FontSize(thermal ? 9 : 11);
                    r.ConstantItem(90).AlignRight().Text(M(b.Total)).Bold().FontSize(thermal ? 9 : 11);
                });
                col.Item().Text(AmountInWords.Rupees(b.Total)).Italic();
                col.Item().PaddingTop(6).AlignCenter().Text("May the blessings be with you · ഭഗവാന്റെ അനുഗ്രഹം ഉണ്ടാകട്ടെ");
                if (!string.IsNullOrWhiteSpace(c.InvoiceFooter)) col.Item().AlignCenter().Text(c.InvoiceFooter);
                col.Item().AlignCenter().Text($"Issued by {b.UserName}").FontColor(Muted(thermal)).FontSize(6.5f);
            });
        });
            }

            if (!withSlips) return;
            for (int i = 0; i < b.Lines.Count; i++)
            {
                if (onlyLine is { } only && i + 1 != only) continue;
                var l = b.Lines[i];
                var n = i + 1;
                doc.Page(page =>
                {
                    Setup(page);
                    page.Content().Column(col =>
                    {
                        col.Item().AlignCenter().Text(c.Name).Bold().FontSize(thermal ? 9 : 11);
                        col.Item().AlignCenter().Text("VAZHIPADU · വഴിപാട്").SemiBold();
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"No: {b.BookingNo}").Bold();
                            r.RelativeItem().AlignRight().Text(bookedAt);
                        });
                        col.Item().Text($"Item {n} of {b.Lines.Count}");
                        col.Item().PaddingVertical(3).LineHorizontal(0.6f);
                        col.Item().Text(l.Offering).Bold().FontSize(thermal ? 12 : 14);
                        if (!string.IsNullOrWhiteSpace(l.OfferingMl)) col.Item().Text(l.OfferingMl).Bold().FontSize(thermal ? 11 : 13);
                        var who = Who(l);
                        if (who.Trim().Length > 0) col.Item().PaddingTop(3).Text(who).Bold().FontSize(thermal ? 9.5f : 11);
                        if (l.Deity is { } d) col.Item().PaddingTop(2).Text($"Deity: {d}{(l.PoojaSlot is { } ps ? " · " + ps : "")}");
                        col.Item().Text(l.Schedule).FontColor(Muted(thermal));
                        col.Item().PaddingVertical(3).LineHorizontal(0.6f);
                        col.Item().Row(r =>
                        {
                            r.RelativeItem().Text(QtyLine(l));
                            r.ConstantItem(thermal ? 55 : 70).AlignRight().Text(M(l.Amount)).Bold().FontSize(thermal ? 9 : 11);
                        });
                    });
                });
            }
        });
    }

    private static string Who(BookingLineDto l) =>
        l.DevoteeName + (l.StarName is { } s ? $" · {s} ({MalayalamNames.Star(l.Star!.Value, true)})" : "");

    private static string QtyLine(BookingLineDto l) => l.Occurrences == 1
        ? $"{Q(l.Qty)} × {M(l.Rate)}"
        : $"{l.Occurrences} days × {Q(l.Qty)} × {M(l.Rate)}" + (l.Dates.Count <= 12 ? $"  ({string.Join(", ", l.Dates.Select(x => x.ToString("dd-MM")))})" : "");

    // ---------------- daily pooja list ----------------

    public async Task<byte[]> PoojaListAsync(DateOnly date, long? deityId, long? slotId, CancellationToken ct)
    {
        var day = await poojaList.DayAsync(date, deityId, slotId, includeCancelled: false, ct);
        var c = await CompanyAsync(ct);
        var cal = day.Calendar;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontFamily(Fonts).FontSize(9));
            page.Header().Column(col =>
            {
                Header(col, c, "POOJA LIST · വഴിപാട് പട്ടിക", false);
                col.Item().AlignCenter().Text($"{date:dddd, dd-MM-yyyy} · {cal.MalayalamDate} ({cal.MonthMl} {cal.MalayalamDay}) · {cal.StarName} ({cal.StarNameMl}) · {cal.TithiName}").SemiBold();
                col.Item().PaddingBottom(4).AlignCenter().Text($"Sunrise {cal.Sunrise} · Sunset {cal.Sunset}{(cal.Festival is { } f ? " · " + f : "")}");
            });
            page.Content().Column(col =>
            {
                if (day.Rows.Count == 0) { col.Item().PaddingTop(20).AlignCenter().Text("No offerings booked for this day."); return; }
                foreach (var g in day.Rows.GroupBy(r => (r.PoojaSlot ?? "Any time", r.Deity ?? "")))
                {
                    col.Item().PaddingTop(8).Text($"{g.Key.Item1}{(g.Key.Item2.Length > 0 ? " — " + g.Key.Item2 : "")}").Bold().FontSize(10.5f);
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(22); cd.RelativeColumn(3); cd.RelativeColumn(3); cd.RelativeColumn(2); cd.ConstantColumn(34); cd.ConstantColumn(60); cd.ConstantColumn(26); });
                        t.Header(h =>
                        {
                            foreach (var title in new[] { "#", "Offering", "Name", "Star", "Qty", "Receipt", "✓" })
                                h.Cell().BorderBottom(0.8f).PaddingVertical(2).Text(title).Bold();
                        });
                        int i = 0;
                        foreach (var r in g)
                        {
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).Text($"{++i}");
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).Text($"{r.Offering}{(r.OfferingMl is { } ml ? "\n" + ml : "")}");
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).Text(r.DevoteeName + (r.Remarks is { Length: > 0 } rm ? $"\n{rm}" : ""));
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).Text(r.StarName is null ? "" : $"{r.StarName}\n{r.StarNameMl}");
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).AlignRight().Text(Q(r.Qty));
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).Text(r.BookingNo).FontSize(8);
                            t.Cell().BorderBottom(0.3f).PaddingVertical(3).AlignCenter().Text(r.Status == "Done" ? "✓" : "☐");
                        }
                    });
                }
                col.Item().PaddingTop(12).Text("To prepare").Bold().FontSize(10.5f);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(4); cd.ConstantColumn(60); cd.ConstantColumn(60); });
                    t.Header(h => { h.Cell().Text("Offering").Bold(); h.Cell().AlignRight().Text("Bookings").Bold(); h.Cell().AlignRight().Text("Quantity").Bold(); });
                    foreach (var s in day.Summary)
                    {
                        t.Cell().Text($"{s.Offering}{(s.OfferingMl is { } ml ? "  " + ml : "")}");
                        t.Cell().AlignRight().Text($"{s.Count}");
                        t.Cell().AlignRight().Text(Q(s.Qty));
                    }
                });
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Printed {clock.Now:dd-MM-yyyy HH:mm} by {user.UserName}").FontColor(Colors.Grey.Darken1).FontSize(7);
                r.RelativeItem().AlignRight().Text(t => { t.Span("Page ").FontSize(7); t.CurrentPageNumber().FontSize(7); t.Span(" of ").FontSize(7); t.TotalPages().FontSize(7); });
            });
        })).GeneratePdf();
    }

    // ---------------- donation receipt ----------------

    public async Task<byte[]> DonationReceiptAsync(long id, PrintFormat? paper, CancellationToken ct)
    {
        var d = await donations.GetAsync(id, ct);
        var c = await CompanyAsync(ct);
        var cal = await calendar.DayAsync(DateOnly.FromDateTime(d.Date.ToOffset(clock.Offset).Date), ct);
        var format = paper ?? (await settings.GetAsync(ct)).DonationReceiptPaper;
        var thermal = ThermalPaper.Is(format);
        return Document.Create(doc => doc.Page(page =>
        {
            if (thermal) ThermalPaper.Apply(page, format);
            else if (format == PrintFormat.A4) { page.Size(PageSizes.A4); page.Margin(32); }
            else { page.Size(PageSizes.A5.Landscape()); page.Margin(22); }
            page.DefaultTextStyle(x => x.FontFamily(Fonts).FontSize(format == PrintFormat.Thermal58 ? 7 : thermal ? 8 : 10));
            page.Content().Column(col =>
            {
                Header(col, c, "DONATION RECEIPT · സംഭാവന രസീത്", thermal);
                if (d.Cancelled) col.Item().AlignCenter().Text("CANCELLED").Bold().FontColor(Colors.Red.Darken2);
                col.Item().Row(r =>
                {
                    r.RelativeItem().Text($"Receipt No: {d.ReceiptNo}").Bold();
                    r.RelativeItem().AlignRight().Text($"{d.Date.ToOffset(clock.Offset):dd-MM-yyyy HH:mm}");
                });
                col.Item().Text(cal.MalayalamDate);
                col.Item().PaddingTop(6).Text(t =>
                {
                    t.Span("Received with thanks from ");
                    t.Span(d.DonorName).Bold();
                    if (d.HouseName is { } h) t.Span($", {h}");
                    if (d.Address is { } a) t.Span($", {a}");
                    if (d.StarName is { } s) t.Span($" ({s})").Bold();
                    t.Span(" the sum of ");
                    t.Span($"Rs. {M(d.Amount)}").Bold();
                    t.Span($" ({AmountInWords.Rupees(d.Amount)}) towards ");
                    t.Span(d.Purpose).Bold();
                    t.Span($", by {d.ReceivedInto}{(d.Reference is { } rf ? " (" + rf + ")" : "")}.");
                });
                if (d.Pan is { } pan) col.Item().Text($"PAN: {pan}");
                if (d.Notes is { } n) col.Item().Text(n);
                col.Item().PaddingTop(16).Row(r =>
                {
                    r.RelativeItem().Text($"Received by {d.UserName}").FontColor(Muted(thermal));
                    r.RelativeItem().AlignRight().Text($"For {c.Name}\n\nAuthorised Signatory");
                });
            });
        })).GeneratePdf();
    }
}
