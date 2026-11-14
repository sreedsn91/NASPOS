using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Application.Documents;
using Kshethra.Application.Inventory;
using Kshethra.Application.Masters;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Validation;

namespace Kshethra.Application.Tools;

public sealed record ImportResult(int Created, int Updated, int Skipped, List<string> Errors, long? OpeningStockDocumentId = null);

/// <summary>Excel import/export of products and ledgers, and weighing-scale PLU files.</summary>
public class ImportExportService(IAppDbContext db, ICurrentUser user, IClock clock, StockQueries stock, DocumentService documents,
    LedgerService ledgers, AuditService audit)
{
    private static readonly string[] ProductColumns =
    [
        "Code", "Name", "RegionalName", "HSN", "Category", "Manufacturer", "Service", "GST%", "Cess%", "AddCessPerUnit", "KFCess%",
        "SaleInclusive", "PurchaseInclusive", "PurchaseRate", "SaleRate", "MRP", "Unit", "AltUnit", "Factor", "Barcode", "ReorderLevel",
        "Rack", "PLU", "ScaleItem", "Discount%", "TrackExpiry", "Active", "OpeningQty",
    ];

    private static readonly string[] LedgerColumns =
        ["Code", "Name", "Mobile", "Phone", "Email", "Address", "City", "StateCode", "GSTIN", "CreditLimit", "CreditDays", "Discount%", "OpeningBalance", "DrCr", "Active", "Type"];

    public async Task<byte[]> ExportProductsAsync(bool template, CancellationToken ct)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Products");
        for (int i = 0; i < ProductColumns.Length; i++) { ws.Cell(1, i + 1).Value = ProductColumns[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        if (!template)
        {
            var products = await db.Products.AsNoTracking().Include(p => p.Category).Include(p => p.Manufacturer).OrderBy(p => p.Code).ToListAsync(ct);
            var onHand = await stock.OnHandByProductAsync(null, null, ct);
            int r = 2;
            foreach (var p in products)
            {
                object?[] v =
                [
                    p.Code, p.Name, p.RegionalName, p.HsnCode, p.Category?.Name, p.Manufacturer?.Name, p.ItemClass == ItemClass.Service ? "Y" : "N",
                    p.IgstRate, p.CessRate, p.AddCessPerUnit, p.KfCessRate, p.SaleTaxInclusive ? "Y" : "N", p.PurchaseTaxInclusive ? "Y" : "N",
                    p.PurchaseRate, p.SaleRate, p.Mrp, p.MainUnit, p.AltUnit, p.ConversionFactor, p.DefaultBarcode, p.ReorderLevel, p.Rack, p.PluCode,
                    p.IsScaleItem ? "Y" : "N", p.DiscountPercent, p.TrackExpiry ? "Y" : "N", p.IsActive ? "Y" : "N", onHand.GetValueOrDefault(p.Id),
                ];
                for (int c = 0; c < v.Length; c++) ws.Cell(r, c + 1).Value = XLCellValue.FromObject(v[c]);
                r++;
            }
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string S(IXLRow row, Dictionary<string, int> map, string col) =>
        map.TryGetValue(col, out var i) ? row.Cell(i).GetFormattedString().Trim() : "";

    private static decimal N(IXLRow row, Dictionary<string, int> map, string col) =>
        decimal.TryParse(S(row, map, col), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static bool B(IXLRow row, Dictionary<string, int> map, string col, bool def = false)
    {
        var s = S(row, map, col).ToUpperInvariant();
        return s == "" ? def : s is "Y" or "YES" or "TRUE" or "1";
    }

    /// <summary>Upserts items by code, creating categories/manufacturers by name; optional opening stock is posted as one document.</summary>
    public async Task<ImportResult> ImportProductsAsync(Stream file, bool postOpeningStock, CancellationToken ct)
    {
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheets.First();
        var header = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        if (!header.ContainsKey("Name")) throw new DomainException("The sheet needs at least a 'Name' column (see the template).");
        var errors = new List<string>();
        int created = 0, updated = 0, skipped = 0;
        var categories = await db.Categories.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, ct);
        var manufacturers = await db.Manufacturers.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, ct);
        var existing = await db.Products.ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase, ct);
        var opening = new List<(Product P, decimal Qty)>();
        long nextCode = existing.Keys.Select(k => long.TryParse(k, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

        await using var tx = await db.BeginAsync(ct);
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            int rn = row.RowNumber();
            try
            {
                string name = S(row, header, "Name");
                if (name.Length == 0) { skipped++; continue; }
                string code = S(row, header, "Code").ToUpperInvariant();
                if (code.Length == 0) code = (nextCode++).ToString("00000");
                bool isNew = !existing.TryGetValue(code, out var p);
                p ??= new Product { Code = code };
                p.Name = name;
                p.RegionalName = NullIfEmpty(S(row, header, "RegionalName")) ?? p.RegionalName;
                p.HsnCode = NullIfEmpty(S(row, header, "HSN")) ?? p.HsnCode;
                string cat = S(row, header, "Category");
                if (cat.Length > 0)
                {
                    if (!categories.TryGetValue(cat, out var c)) { c = new Category { Name = cat }; db.Categories.Add(c); categories[cat] = c; }
                    p.Category = c;
                }
                string mnf = S(row, header, "Manufacturer");
                if (mnf.Length > 0)
                {
                    if (!manufacturers.TryGetValue(mnf, out var m)) { m = new Manufacturer { Name = mnf }; db.Manufacturers.Add(m); manufacturers[mnf] = m; }
                    p.Manufacturer = m;
                }
                p.ItemClass = B(row, header, "Service") ? ItemClass.Service : ItemClass.Goods;
                if (header.ContainsKey("GST%")) { decimal g = N(row, header, "GST%"); p.IgstRate = g; p.CgstRate = g / 2; p.SgstRate = g / 2; }
                if (header.ContainsKey("Cess%")) p.CessRate = N(row, header, "Cess%");
                if (header.ContainsKey("AddCessPerUnit")) p.AddCessPerUnit = N(row, header, "AddCessPerUnit");
                if (header.ContainsKey("KFCess%")) p.KfCessRate = N(row, header, "KFCess%");
                p.SaleTaxInclusive = B(row, header, "SaleInclusive", p.SaleTaxInclusive);
                p.PurchaseTaxInclusive = B(row, header, "PurchaseInclusive", p.PurchaseTaxInclusive);
                if (header.ContainsKey("PurchaseRate")) p.PurchaseRate = N(row, header, "PurchaseRate");
                if (header.ContainsKey("SaleRate")) p.SaleRate = N(row, header, "SaleRate");
                if (header.ContainsKey("MRP")) p.Mrp = N(row, header, "MRP");
                p.MainUnit = NullIfEmpty(S(row, header, "Unit"))?.ToUpperInvariant() ?? (isNew ? "NOS" : p.MainUnit);
                p.AltUnit = NullIfEmpty(S(row, header, "AltUnit"))?.ToUpperInvariant() ?? p.AltUnit;
                decimal factor = N(row, header, "Factor");
                p.ConversionFactor = factor > 0 ? factor : (isNew ? 1 : p.ConversionFactor);
                p.DefaultBarcode = NullIfEmpty(S(row, header, "Barcode")) ?? p.DefaultBarcode;
                if (header.ContainsKey("ReorderLevel")) p.ReorderLevel = N(row, header, "ReorderLevel");
                p.Rack = NullIfEmpty(S(row, header, "Rack")) ?? p.Rack;
                p.PluCode = NullIfEmpty(S(row, header, "PLU")) ?? p.PluCode;
                p.IsScaleItem = B(row, header, "ScaleItem", p.IsScaleItem);
                if (header.ContainsKey("Discount%")) p.DiscountPercent = N(row, header, "Discount%");
                p.TrackExpiry = B(row, header, "TrackExpiry", p.TrackExpiry);
                p.IsActive = B(row, header, "Active", true);
                if (p.Mrp > 0 && p.SaleRate > p.Mrp) throw new DomainException("sale rate exceeds MRP");
                if (p.IsScaleItem && string.IsNullOrEmpty(p.PluCode)) throw new DomainException("scale item needs a PLU");
                if (isNew) { db.Products.Add(p); existing[code] = p; created++; } else updated++;
                decimal oq = N(row, header, "OpeningQty");
                if (postOpeningStock && oq > 0 && p.ItemClass == ItemClass.Goods) opening.Add((p, oq));
            }
            catch (Exception ex) when (ex is DomainException or FormatException or InvalidCastException)
            {
                errors.Add($"Row {rn}: {ex.Message}");
            }
        }
        if (errors.Count > 0) throw new DomainException("Import stopped; nothing was saved. " + string.Join(" | ", errors.Take(20)), "import_errors");
        audit.Log(Screens.Product, "Import", $"{created} created, {updated} updated");
        await db.SaveChangesAsync(ct);

        long? docId = null;
        if (opening.Count > 0)
        {
            var dto = new TradeDocumentDto
            {
                VoucherType = VoucherType.OpeningStock, Date = clock.Now, Remarks = "Imported opening stock", AcknowledgeWarnings = true,
                Lines = opening.Select(o => new TradeLineDto { ProductId = o.P.Id, Qty = o.Qty, Rate = o.P.PurchaseRate, Mrp = o.P.Mrp, SaleRate = o.P.SaleRate }).ToList(),
            };
            docId = (await documents.SaveAsync(dto, null, ct)).Id;
        }
        await tx.CommitAsync(ct);
        return new ImportResult(created, updated, skipped, errors, docId);
    }

    public async Task<byte[]> ExportLedgersAsync(PartyType type, bool template, CancellationToken ct)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(type == PartyType.Supplier ? "Suppliers" : "Customers");
        for (int i = 0; i < LedgerColumns.Length; i++) { ws.Cell(1, i + 1).Value = LedgerColumns[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        if (!template)
        {
            var rows = await db.Ledgers.AsNoTracking().Where(l => l.PartyType == type).OrderBy(l => l.Code).ToListAsync(ct);
            var openings = (await ledgers.OpeningBalancesAsync(ct)).ToDictionary(o => o.LedgerId);
            int r = 2;
            foreach (var l in rows)
            {
                var ob = openings.GetValueOrDefault(l.Id);
                object?[] v = [l.Code, l.Name, l.Mobile, l.Phone, l.Email, l.Address, l.City, l.StateCode, l.Gstin, l.CreditLimit, l.CreditDays, l.DiscountPercent,
                    ob?.Amount ?? 0, ob?.Side.ToString() ?? "Dr", l.IsActive ? "Y" : "N", l.IsCorporate ? "Corporate" : "Individual"];
                for (int c = 0; c < v.Length; c++) ws.Cell(r, c + 1).Value = XLCellValue.FromObject(v[c]);
                r++;
            }
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<ImportResult> ImportLedgersAsync(Stream file, PartyType type, CancellationToken ct)
    {
        using var wb = new XLWorkbook(file);
        var ws = wb.Worksheets.First();
        var header = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim(), c => c.Address.ColumnNumber, StringComparer.OrdinalIgnoreCase);
        if (!header.ContainsKey("Name")) throw new DomainException("The sheet needs at least a 'Name' column (see the template).");
        var group = await db.AccountGroups.FirstAsync(g => g.SystemCode == (type == PartyType.Supplier ? SystemGroup.SundryCreditors : SystemGroup.SundryDebtors), ct);
        var existing = await db.Ledgers.AsNoTracking().Where(l => l.PartyType == type).ToDictionaryAsync(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase, ct);
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();
        await using var tx = await db.BeginAsync(ct);
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            string name = S(row, header, "Name");
            if (name.Length == 0) { skipped++; continue; }
            try
            {
                string code = S(row, header, "Code").ToUpperInvariant();
                long id = code.Length > 0 && existing.TryGetValue(code, out var eid) ? eid : 0;
                var dto = id > 0 ? await ledgers.GetAsync(id, ct) : new LedgerDto { AccountGroupId = group.Id, PartyType = type };
                dto.Code = code; dto.Name = name; dto.AccountGroupId = group.Id; dto.RowVersion = null;
                dto.Mobile = NullIfEmpty(S(row, header, "Mobile")) ?? dto.Mobile;
                dto.Phone = NullIfEmpty(S(row, header, "Phone")) ?? dto.Phone;
                dto.Email = NullIfEmpty(S(row, header, "Email")) ?? dto.Email;
                dto.Address = NullIfEmpty(S(row, header, "Address")) ?? dto.Address;
                dto.City = NullIfEmpty(S(row, header, "City")) ?? dto.City;
                dto.StateCode = NullIfEmpty(S(row, header, "StateCode"))?.PadLeft(2, '0') ?? dto.StateCode;
                dto.Gstin = NullIfEmpty(S(row, header, "GSTIN")) ?? dto.Gstin;
                if (header.ContainsKey("CreditLimit")) dto.CreditLimit = N(row, header, "CreditLimit");
                if (header.ContainsKey("CreditDays")) dto.CreditDays = (int)N(row, header, "CreditDays");
                if (header.ContainsKey("Discount%")) dto.DiscountPercent = N(row, header, "Discount%");
                if (header.ContainsKey("OpeningBalance"))
                {
                    dto.OpeningBalance = Math.Abs(N(row, header, "OpeningBalance"));
                    dto.OpeningSide = S(row, header, "DrCr").Equals("Cr", StringComparison.OrdinalIgnoreCase) ? EntrySide.Cr : EntrySide.Dr;
                }
                dto.IsActive = B(row, header, "Active", true);
                // Type: Individual (default) or Corporate; corporate names may repeat.
                if (header.ContainsKey("Type")) dto.IsCorporate = S(row, header, "Type").StartsWith("C", StringComparison.OrdinalIgnoreCase);
                await ledgers.SaveAsync(dto, ct);
                if (id > 0) updated++; else created++;
            }
            catch (DomainException ex) { errors.Add($"Row {row.RowNumber()}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new DomainException("Import stopped; nothing was saved. " + string.Join(" | ", errors.Take(20)), "import_errors");
        audit.Log(type == PartyType.Supplier ? Screens.Supplier : Screens.Customer, "Import", $"{created} created, {updated} updated");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(created, updated, skipped, errors);
    }

    /// <summary>PLU file for weighing scales: PLU,Code,Name,Price,Unit (CSV).</summary>
    public async Task<byte[]> ExportPluAsync(CancellationToken ct)
    {
        var items = await db.Products.AsNoTracking().Where(p => p.IsScaleItem && p.IsActive && p.PluCode != null).OrderBy(p => p.PluCode).ToListAsync(ct);
        var sb = new StringBuilder("PLU,Code,Name,Price,Unit\r\n");
        foreach (var p in items)
            sb.Append(CultureInfo.InvariantCulture, $"{p.PluCode},{Csv(p.Code)},{Csv(p.Name)},{p.SaleRate:0.00},{p.MainUnit}\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Assigns PLU codes (and optionally prices) from a CSV with columns Code,PLU[,Price].</summary>
    public async Task<ImportResult> ImportPluAsync(Stream file, CancellationToken ct)
    {
        using var reader = new StreamReader(file);
        var products = await db.Products.ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase, ct);
        int updated = 0, skipped = 0;
        var errors = new List<string>();
        int line = 0;
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            line++;
            var parts = text.Split(',').Select(p => p.Trim().Trim('"')).ToArray();
            if (line == 1 && parts[0].Equals("Code", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length < 2 || !products.TryGetValue(parts[0], out var p)) { skipped++; continue; }
            if (products.Values.Any(x => x.PluCode == parts[1] && x.Id != p.Id)) { errors.Add($"Line {line}: PLU {parts[1]} already used."); continue; }
            p.PluCode = parts[1];
            p.IsScaleItem = true;
            if (parts.Length > 2 && decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && price > 0) p.SaleRate = price;
            updated++;
        }
        if (errors.Count > 0) throw new DomainException(string.Join(" | ", errors.Take(20)), "import_errors");
        audit.Log(Screens.Product, "Import", $"PLU: {updated} items");
        await db.SaveChangesAsync(ct);
        return new ImportResult(0, updated, skipped, errors);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
