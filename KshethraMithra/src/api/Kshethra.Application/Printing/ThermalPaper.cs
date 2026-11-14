using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Kshethra.Domain;

namespace Kshethra.Application.Printing;

/// <summary>
/// Page setup for thermal receipts. Windows receipt-printer drivers offer roll "papers" such as 80(72.1) x 297 mm and
/// 58(48) x 297 mm. A short PDF page is centred on that sheet by the browser (blank paper above the receipt) and can be
/// clipped at the sides, so receipts are drawn on a full 297 mm page of the printable width instead: the browser prints
/// it at the top at actual size, and the driver stops feeding after the last printed line. 3 inch rolls print 70 mm of
/// content on a 72 mm page, 2 inch rolls 46 mm on 48 mm. Longer receipts simply continue on the next page.
/// </summary>
public static class ThermalPaper
{
    private const float Mm = 72f / 25.4f;
    private const float Side = 1 * Mm;

    public static bool Is(PrintFormat f) => f is PrintFormat.Thermal80 or PrintFormat.Thermal58;
    public static bool Narrow(PrintFormat f) => f == PrintFormat.Thermal58;

    /// <param name="continuous">Page exactly as tall as its content (for direct raster printing, which has no browser page).</param>
    public static void Apply(PageDescriptor page, PrintFormat f, bool continuous = false)
    {
        var width = Narrow(f) ? 48 * Mm : 72 * Mm;
        if (continuous) page.ContinuousSize(width); else page.Size(width, 297 * Mm);
        page.MarginHorizontal(Side);
        page.MarginVertical(2 * Mm);
    }
}
