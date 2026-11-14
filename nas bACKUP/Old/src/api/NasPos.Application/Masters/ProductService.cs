using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Inventory;
using NasPos.Application.Settings;
using NasPos.Domain;
using NasPos.Domain.Calculation;
using NasPos.Domain.Entities;
using NasPos.Domain.Validation;

namespace NasPos.Application.Masters;

public sealed class ProductDto
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    public string? HsnCode { get; set; }
    public long? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public long? ManufacturerId { get; set; }
    public string? ManufacturerName { get; set; }
    public ItemClass ItemClass { get; set; } = ItemClass.Goods;
    public decimal GstRate { get; set; }
    public decimal CgstRate { get; set; }
    public decimal SgstRate { get; set; }
    public decimal IgstRate { get; set; }
    public bool HasSecondaryTax { get; set; }
    public decimal SecondaryTaxThreshold { get; set; }
    public decimal Cgst2Rate { get; set; }
    public decimal Sgst2Rate { get; set; }
    public decimal Igst2Rate { get; set; }
    public decimal CessRate { get; set; }
    public decimal AddCessPerUnit { get; set; }
    public decimal KfCessRate { get; set; }
    public bool SaleTaxInclusive { get; set; }
    public bool PurchaseTaxInclusive { get; set; }
    public decimal PurchaseRate { get; set; }
    public decimal SaleRate { get; set; }
    public decimal Mrp { get; set; }
    public decimal SaleMarginPercent { get; set; }
    public decimal MrpMarginPercent { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal CommissionPercent { get; set; }
    public string MainUnit { get; set; } = "NOS";
    public string? AltUnit { get; set; }
    public decimal ConversionFactor { get; set; } = 1;
    public string? DefaultBarcode { get; set; }
    public decimal ReorderLevel { get; set; }
    public string? Rack { get; set; }
    public bool TrackExpiry { get; set; }
    public bool IsScaleItem { get; set; }
    public string? PluCode { get; set; }
    public decimal PiecesPerBox { get; set; }
    public decimal SqftPerBox { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
    public decimal Stock { get; set; }
    public bool HasImage { get; set; }
    public string? RowVersion { get; set; }
}

public sealed record ProductListItem(long Id, string Code, string Name, string? CategoryName, string? HsnCode, decimal GstRate,
    decimal PurchaseRate, decimal SaleRate, decimal Mrp, string MainUnit, decimal Stock, decimal ReorderLevel, bool IsActive, ItemClass ItemClass);

/// <summary>A sellable row for billing screens: product + (optional) batch with live stock and the applicable price.</summary>
public sealed class ProductLookup
{
    public long ProductId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    public string? HsnCode { get; set; }
    public long? CategoryId { get; set; }
    public ItemClass ItemClass { get; set; }
    public string MainUnit { get; set; } = "";
    public string? AltUnit { get; set; }
    public decimal ConversionFactor { get; set; } = 1;
    public decimal CgstRate { get; set; }
    public decimal SgstRate { get; set; }
    public decimal IgstRate { get; set; }
    public bool HasSecondaryTax { get; set; }
    public decimal SecondaryTaxThreshold { get; set; }
    public decimal Cgst2Rate { get; set; }
    public decimal Sgst2Rate { get; set; }
    public decimal Igst2Rate { get; set; }
    public decimal CessRate { get; set; }
    public decimal AddCessPerUnit { get; set; }
    public decimal KfCessRate { get; set; }
    public bool SaleTaxInclusive { get; set; }
    public bool PurchaseTaxInclusive { get; set; }
    public decimal PurchaseRate { get; set; }
    public decimal SaleRate { get; set; }
    public decimal Mrp { get; set; }
    public decimal Cost { get; set; }
    public decimal DiscountPercent { get; set; }
    public bool TrackExpiry { get; set; }
    public bool IsScaleItem { get; set; }
    public decimal PiecesPerBox { get; set; }
    public decimal SqftPerBox { get; set; }
    public long? BatchId { get; set; }
    public string? BatchCode { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public decimal Stock { get; set; }
    /// <summary>Price to use: offer, else price list, else batch/product sale rate.</summary>
    public decimal Price { get; set; }
    public string PriceSource { get; set; } = "Rate";
    /// <summary>Weighing-scale label: quantity decoded from the barcode (weight, or price ÷ rate).</summary>
    public decimal? ScaleQty { get; set; }
    public bool HasImage { get; set; }
}

public sealed record BatchDto(long Id, long ProductId, string Code, decimal PurchaseRate, decimal SaleRate, decimal Mrp, decimal Cost,
    DateOnly? ExpiryDate, bool IsActive, decimal Stock);

public sealed record PosTile(long ProductId, string Code, string Name, string? RegionalName, long? CategoryId, decimal Price, bool HasImage, decimal Stock);

public class ProductService(IAppDbContext db, ICurrentUser user, IClock clock, SettingsService settingsService, StockQueries stock,
    AuditService audit, IRealtimeNotifier notifier)
{
    public async Task<PagedResult<ProductListItem>> ListAsync(string? search, long? categoryId, bool? active, bool? belowReorder, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Products.AsNoTracking().AsQueryable();
        if (categoryId is not null) q = q.Where(p => p.CategoryId == categoryId);
        if (active is not null) q = q.Where(p => p.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p => p.Name.Contains(s) || p.Code.Contains(s) || p.DefaultBarcode == s || (p.HsnCode != null && p.HsnCode.StartsWith(s))
                             || db.Batches.Any(b => b.ProductId == p.Id && b.Code == s));
        }
        long fy = user.FinancialYearId;
        var projected = q.Select(p => new
        {
            p,
            Stock = db.StockMovements.Where(m => m.ProductId == p.Id && m.FinancialYearId == fy).Sum(m => (decimal?)(m.QtyIn - m.QtyOut)) ?? 0,
            Cat = p.Category != null ? p.Category.Name : null,
        });
        if (belowReorder == true) projected = projected.Where(x => x.p.ReorderLevel > 0 && x.Stock <= x.p.ReorderLevel);
        int total = await projected.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 1000);
        var rows = await projected.OrderBy(x => x.p.Name).Skip((Math.Max(page, 1) - 1) * size).Take(size)
            .Select(x => new ProductListItem(x.p.Id, x.p.Code, x.p.Name, x.Cat, x.p.HsnCode, x.p.IgstRate, x.p.PurchaseRate, x.p.SaleRate, x.p.Mrp,
                x.p.MainUnit, x.Stock, x.p.ReorderLevel, x.p.IsActive, x.p.ItemClass))
            .ToListAsync(ct);
        return new PagedResult<ProductListItem>(rows, total, page, size);
    }

    public async Task<ProductDto> GetAsync(long id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().Include(x => x.Category).Include(x => x.Manufacturer).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Product not found.");
        var dto = ToDto(p);
        dto.Stock = (await stock.OnHandByProductAsync([id], null, ct)).GetValueOrDefault(id);
        dto.HasImage = await db.ProductImages.AnyAsync(i => i.ProductId == id, ct);
        return dto;
    }

    private static ProductDto ToDto(Product p) => new()
    {
        Id = p.Id, Code = p.Code, Name = p.Name, RegionalName = p.RegionalName, HsnCode = p.HsnCode, CategoryId = p.CategoryId,
        CategoryName = p.Category?.Name, ManufacturerId = p.ManufacturerId, ManufacturerName = p.Manufacturer?.Name, ItemClass = p.ItemClass,
        GstRate = p.IgstRate, CgstRate = p.CgstRate, SgstRate = p.SgstRate, IgstRate = p.IgstRate, HasSecondaryTax = p.HasSecondaryTax,
        SecondaryTaxThreshold = p.SecondaryTaxThreshold, Cgst2Rate = p.Cgst2Rate, Sgst2Rate = p.Sgst2Rate, Igst2Rate = p.Igst2Rate,
        CessRate = p.CessRate, AddCessPerUnit = p.AddCessPerUnit, KfCessRate = p.KfCessRate, SaleTaxInclusive = p.SaleTaxInclusive,
        PurchaseTaxInclusive = p.PurchaseTaxInclusive, PurchaseRate = p.PurchaseRate, SaleRate = p.SaleRate, Mrp = p.Mrp,
        SaleMarginPercent = p.SaleMarginPercent, MrpMarginPercent = p.MrpMarginPercent, DiscountPercent = p.DiscountPercent,
        CommissionPercent = p.CommissionPercent, MainUnit = p.MainUnit, AltUnit = p.AltUnit, ConversionFactor = p.ConversionFactor,
        DefaultBarcode = p.DefaultBarcode, ReorderLevel = p.ReorderLevel, Rack = p.Rack, TrackExpiry = p.TrackExpiry,
        IsScaleItem = p.IsScaleItem, PluCode = p.PluCode, PiecesPerBox = p.PiecesPerBox, SqftPerBox = p.SqftPerBox, Remarks = p.Remarks,
        IsActive = p.IsActive, RowVersion = Convert.ToBase64String(p.RowVersion),
    };

    public async Task<long> SaveAsync(ProductDto dto, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new DomainException("Item name is required.");
        if (dto.ConversionFactor <= 0) throw new DomainException("Conversion factor must be positive.");
        if (!string.IsNullOrWhiteSpace(dto.AltUnit) && dto.ConversionFactor == 1) throw new DomainException("Enter the conversion factor for the alternate unit.", "alt_unit");
        if (string.IsNullOrWhiteSpace(dto.AltUnit) && dto.ConversionFactor != 1) throw new DomainException("Enter the alternate unit name for the conversion factor.", "alt_unit");
        if (dto.IsScaleItem && string.IsNullOrWhiteSpace(dto.PluCode)) throw new DomainException("A PLU code is required for weighing-scale items.", "plu_required");
        if (dto.CgstRate < 0 || dto.SgstRate < 0 || dto.IgstRate < 0 || dto.CessRate < 0 || dto.KfCessRate < 0 || dto.AddCessPerUnit < 0)
            throw new DomainException("Tax rates cannot be negative.");
        if (dto.PurchaseRate < 0 || dto.SaleRate < 0 || dto.Mrp < 0) throw new DomainException("Rates cannot be negative.");
        if (dto.Mrp > 0 && dto.SaleRate > dto.Mrp) throw new DomainException("Sale rate cannot exceed MRP.", "rate_above_mrp");

        Product p;
        if (dto.Id > 0)
        {
            p = await db.Products.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Product not found.");
            if (!string.IsNullOrEmpty(dto.RowVersion) && !Convert.FromBase64String(dto.RowVersion).SequenceEqual(p.RowVersion))
                throw new ConflictException("This item was changed by another user. Reload and try again.");
        }
        else { p = new Product(); db.Products.Add(p); }

        string code = string.IsNullOrWhiteSpace(dto.Code) ? await NextCodeAsync(ct) : dto.Code.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(x => x.Code == code && x.Id != dto.Id, ct)) throw new DomainException($"Item code {code} is already used.", "duplicate");
        string? barcode = string.IsNullOrWhiteSpace(dto.DefaultBarcode) ? null : dto.DefaultBarcode.Trim();
        if (barcode is not null && await db.Products.AnyAsync(x => x.DefaultBarcode == barcode && x.Id != dto.Id, ct))
            throw new DomainException($"Barcode {barcode} is used by another item.", "duplicate");
        if (barcode is not null && await db.Batches.AnyAsync(b => b.Code == barcode && b.ProductId != dto.Id, ct))
            throw new DomainException($"Barcode {barcode} is used by a batch of another item.", "duplicate");
        string? plu = string.IsNullOrWhiteSpace(dto.PluCode) ? null : dto.PluCode.Trim();
        if (plu is not null && await db.Products.AnyAsync(x => x.PluCode == plu && x.Id != dto.Id, ct)) throw new DomainException($"PLU {plu} is already used.", "duplicate");

        p.Code = code; p.Name = name; p.RegionalName = dto.RegionalName?.Trim(); p.HsnCode = dto.HsnCode?.Trim();
        p.CategoryId = dto.CategoryId; p.ManufacturerId = dto.ManufacturerId; p.ItemClass = dto.ItemClass;
        // One GST % entered: split into CGST/SGST halves and IGST.
        if (dto.GstRate > 0 && dto.CgstRate == 0 && dto.SgstRate == 0 && dto.IgstRate == 0)
        {
            dto.IgstRate = dto.GstRate; dto.CgstRate = dto.GstRate / 2; dto.SgstRate = dto.GstRate / 2;
        }
        if (dto.CgstRate + dto.SgstRate != dto.IgstRate && dto.IgstRate > 0) throw new DomainException("CGST + SGST must equal IGST.", "tax_mismatch");
        p.CgstRate = dto.CgstRate; p.SgstRate = dto.SgstRate; p.IgstRate = dto.IgstRate;
        p.HasSecondaryTax = dto.HasSecondaryTax; p.SecondaryTaxThreshold = dto.SecondaryTaxThreshold;
        p.Cgst2Rate = dto.Cgst2Rate; p.Sgst2Rate = dto.Sgst2Rate; p.Igst2Rate = dto.Igst2Rate;
        p.CessRate = dto.CessRate; p.AddCessPerUnit = dto.AddCessPerUnit; p.KfCessRate = dto.KfCessRate;
        p.SaleTaxInclusive = dto.SaleTaxInclusive; p.PurchaseTaxInclusive = dto.PurchaseTaxInclusive;
        p.PurchaseRate = dto.PurchaseRate; p.SaleRate = dto.SaleRate; p.Mrp = dto.Mrp;
        p.SaleMarginPercent = dto.PurchaseRate > 0 ? DocumentCalculator.R2((dto.SaleRate - dto.PurchaseRate) * 100 / dto.PurchaseRate) : 0;
        p.MrpMarginPercent = dto.PurchaseRate > 0 && dto.Mrp > 0 ? DocumentCalculator.R2((dto.Mrp - dto.PurchaseRate) * 100 / dto.PurchaseRate) : 0;
        p.DiscountPercent = dto.DiscountPercent; p.CommissionPercent = dto.CommissionPercent;
        p.MainUnit = string.IsNullOrWhiteSpace(dto.MainUnit) ? "NOS" : dto.MainUnit.Trim().ToUpperInvariant();
        p.AltUnit = string.IsNullOrWhiteSpace(dto.AltUnit) ? null : dto.AltUnit.Trim().ToUpperInvariant();
        p.ConversionFactor = dto.ConversionFactor;
        p.DefaultBarcode = barcode; p.ReorderLevel = dto.ReorderLevel; p.Rack = dto.Rack?.Trim();
        p.TrackExpiry = dto.TrackExpiry && settings.EnableExpiry; p.IsScaleItem = dto.IsScaleItem; p.PluCode = plu;
        p.PiecesPerBox = dto.PiecesPerBox; p.SqftPerBox = dto.SqftPerBox; p.Remarks = dto.Remarks?.Trim(); p.IsActive = dto.IsActive;

        audit.Log(Screens.Product, dto.Id == 0 ? "Add" : "Edit", $"{p.Code} {p.Name}");
        await db.SaveChangesAsync(ct);
        await notifier.MasterChangedAsync(user.CompanyId, "product");
        return p.Id;
    }

    public async Task<string> NextCodeAsync(CancellationToken ct)
    {
        var codes = await db.Products.Select(p => p.Code).ToListAsync(ct);
        long max = codes.Select(c => long.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString("00000");
    }

    /// <summary>Delete is blocked once an item appears in any transaction.</summary>
    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product not found.");
        bool used = await db.TradeDocumentLines.AnyAsync(l => l.ProductId == id, ct) || await db.StockMovements.AnyAsync(m => m.ProductId == id, ct)
                    || await db.BomLines.AnyAsync(b => b.ProductId == id, ct) || await db.ItemOffers.AnyAsync(o => o.ProductId == id, ct);
        if (used) throw new DomainException("This item is connected with transactions and cannot be deleted. Deactivate it instead.", "in_use");
        await db.PriceListItems.Where(x => x.ProductId == id).ExecuteDeleteAsync(ct);
        await db.Batches.Where(b => b.ProductId == id).ExecuteDeleteAsync(ct);
        db.Products.Remove(p);
        audit.Log(Screens.Product, "Delete", $"{p.Code} {p.Name}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<BatchDto>> BatchesAsync(long productId, bool activeOnly, CancellationToken ct)
    {
        var batches = await db.Batches.AsNoTracking().Where(b => b.ProductId == productId && (!activeOnly || b.IsActive)).OrderByDescending(b => b.Id).ToListAsync(ct);
        var onHand = await stock.OnHandByBatchAsync(batches.Select(b => b.Id), null, ct);
        return batches.Select(b => new BatchDto(b.Id, b.ProductId, b.Code, b.PurchaseRate, b.SaleRate, b.Mrp, b.Cost, b.ExpiryDate, b.IsActive,
            onHand.GetValueOrDefault(b.Id))).ToList();
    }

    public async Task UpdateBatchAsync(BatchDto dto, CancellationToken ct)
    {
        var b = await db.Batches.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Batch not found.");
        if (dto.Mrp > 0 && dto.SaleRate > dto.Mrp) throw new DomainException("Sale rate cannot exceed MRP.");
        b.SaleRate = dto.SaleRate; b.Mrp = dto.Mrp; b.ExpiryDate = dto.ExpiryDate; b.IsActive = dto.IsActive;
        audit.Log(Screens.Product, "Edit", $"Batch {b.Code}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Billing lookup: exact barcode/batch → product code → PLU → weighing-scale label → name search. One row per batch
    /// with live stock and the price that applies (offer inside its window, else price list, else sale rate).
    /// </summary>
    public async Task<List<ProductLookup>> LookupAsync(string? term, long? priceListId, bool purchase, bool exact, int take, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        term = term?.Trim();
        if (string.IsNullOrEmpty(term)) return [];
        decimal? scaleValue = null;
        bool scaleIsPrice = false;

        var batchHits = await db.Batches.AsNoTracking().Where(b => b.Code == term && b.IsActive && b.Product!.IsActive).Select(b => b.Id).ToListAsync(ct);
        List<long> productIds;
        if (batchHits.Count > 0)
            productIds = await db.Batches.Where(b => batchHits.Contains(b.Id)).Select(b => b.ProductId).Distinct().ToListAsync(ct);
        else
        {
            productIds = await db.Products.AsNoTracking().Where(p => p.IsActive && (p.Code == term || p.DefaultBarcode == term || p.PluCode == term))
                .Select(p => p.Id).ToListAsync(ct);
            if (productIds.Count == 0 && settings.EnableWeighingScale && ScaleBarcodeParser.TryParse(term, settings.WeighingScaleFormat) is { } scale)
            {
                var code = scale.ItemCode.TrimStart('0');
                productIds = await db.Products.AsNoTracking()
                    .Where(p => p.IsActive && (p.PluCode == scale.ItemCode || p.PluCode == code || p.Code == scale.ItemCode || p.Code == code))
                    .Select(p => p.Id).ToListAsync(ct);
                scaleValue = scale.Value;
                scaleIsPrice = scale.IsPrice;
            }
            if (productIds.Count == 0 && !exact)
                productIds = await db.Products.AsNoTracking().Where(p => p.IsActive && (p.Name.Contains(term) || p.Code.StartsWith(term) || (p.RegionalName != null && p.RegionalName.Contains(term))))
                    .OrderBy(p => p.Name).Select(p => p.Id).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        }
        if (productIds.Count == 0) return [];

        var rows = await BuildLookupsAsync(productIds, batchHits.Count > 0 ? batchHits : null, priceListId, purchase, settings, ct);
        if (scaleValue is not null)
            foreach (var r in rows)
                r.ScaleQty = scaleIsPrice ? (r.Price > 0 ? Math.Round(scaleValue.Value / r.Price, 3, MidpointRounding.AwayFromZero) : 0) : scaleValue;
        return rows;
    }

    public async Task<List<ProductLookup>> BuildLookupsAsync(List<long> productIds, List<long>? onlyBatches, long? priceListId, bool purchase,
        CompanySettings settings, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        var batches = await db.Batches.AsNoTracking().Where(b => productIds.Contains(b.ProductId) && b.IsActive).ToListAsync(ct);
        if (onlyBatches is not null) batches = batches.Where(b => onlyBatches.Contains(b.Id)).ToList();
        var onHand = await stock.OnHandByBatchAsync(batches.Select(b => b.Id), null, ct);
        var withImages = await db.ProductImages.Where(i => productIds.Contains(i.ProductId)).Select(i => i.ProductId).Distinct().ToListAsync(ct);
        var now = clock.Now;
        var offers = settings.EnableItemOffers && !purchase
            ? await db.ItemOffers.AsNoTracking().Where(o => productIds.Contains(o.ProductId) && o.IsActive && o.StartAt <= now && o.EndAt >= now).ToListAsync(ct)
            : [];
        var listPrices = settings.EnablePriceList && priceListId is not null && !purchase
            ? await db.PriceListItems.AsNoTracking().Where(x => x.PriceListId == priceListId && productIds.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, x => x.Price, ct)
            : [];

        var result = new List<ProductLookup>();
        foreach (var p in products.OrderBy(p => productIds.IndexOf(p.Id)))
        {
            var pb = batches.Where(b => b.ProductId == p.Id).OrderByDescending(b => onHand.GetValueOrDefault(b.Id) > 0).ThenByDescending(b => b.Id).ToList();
            if (pb.Count == 0 || p.ItemClass == ItemClass.Service)
                result.Add(Make(p, null, 0));
            else if (!settings.EnableBatch)
                result.Add(Make(p, pb[0], pb.Sum(b => onHand.GetValueOrDefault(b.Id))));
            else
                foreach (var b in pb) result.Add(Make(p, b, onHand.GetValueOrDefault(b.Id)));
        }
        return result;

        ProductLookup Make(Product p, Batch? b, decimal qty)
        {
            decimal rate = purchase ? (b?.PurchaseRate ?? p.PurchaseRate) : (b?.SaleRate > 0 ? b.SaleRate : p.SaleRate);
            var r = new ProductLookup
            {
                ProductId = p.Id, Code = p.Code, Name = p.Name, RegionalName = p.RegionalName, HsnCode = p.HsnCode, CategoryId = p.CategoryId,
                ItemClass = p.ItemClass, MainUnit = p.MainUnit, AltUnit = settings.EnableUnitConversion ? p.AltUnit : null,
                ConversionFactor = p.ConversionFactor, CgstRate = p.CgstRate, SgstRate = p.SgstRate, IgstRate = p.IgstRate,
                HasSecondaryTax = p.HasSecondaryTax, SecondaryTaxThreshold = p.SecondaryTaxThreshold, Cgst2Rate = p.Cgst2Rate,
                Sgst2Rate = p.Sgst2Rate, Igst2Rate = p.Igst2Rate, CessRate = settings.EnableCess ? p.CessRate : 0,
                AddCessPerUnit = settings.EnableAddCess ? p.AddCessPerUnit : 0, KfCessRate = p.KfCessRate,
                SaleTaxInclusive = p.SaleTaxInclusive, PurchaseTaxInclusive = p.PurchaseTaxInclusive,
                PurchaseRate = b?.PurchaseRate ?? p.PurchaseRate, SaleRate = b?.SaleRate > 0 ? b.SaleRate : p.SaleRate,
                Mrp = b?.Mrp > 0 ? b.Mrp : p.Mrp, Cost = b?.Cost > 0 ? b.Cost : p.PurchaseRate, DiscountPercent = p.DiscountPercent,
                TrackExpiry = p.TrackExpiry, IsScaleItem = p.IsScaleItem, PiecesPerBox = p.PiecesPerBox, SqftPerBox = p.SqftPerBox,
                BatchId = b?.Id, BatchCode = b?.Code, ExpiryDate = b?.ExpiryDate, Stock = qty, Price = rate, HasImage = withImages.Contains(p.Id),
            };
            var offer = offers.Where(o => o.ProductId == p.Id && (o.BatchId == null || o.BatchId == b?.Id)).OrderBy(o => o.NetRate).FirstOrDefault();
            if (offer is not null && offer.IsActiveAt(now)) { r.Price = offer.NetRate; r.PriceSource = "Offer"; }
            else if (listPrices.TryGetValue(p.Id, out var lp) && lp > 0) { r.Price = lp; r.PriceSource = "PriceList"; }
            return r;
        }
    }

    public async Task<List<PosTile>> PosTilesAsync(long? categoryId, long? priceListId, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var ids = await db.Products.AsNoTracking().Where(p => p.IsActive && (categoryId == null || p.CategoryId == categoryId))
            .OrderBy(p => p.Name).Select(p => p.Id).Take(300).ToListAsync(ct);
        var lookups = await BuildLookupsAsync(ids, null, priceListId, false, settings, ct);
        return lookups.GroupBy(l => l.ProductId).Select(g =>
        {
            var f = g.First();
            return new PosTile(f.ProductId, f.Code, f.Name, f.RegionalName, f.CategoryId, f.Price, f.HasImage, g.Sum(x => x.Stock));
        }).ToList();
    }

    /// <summary>Past purchase/sale rates of an item for a party (supplier/customer rate grid).</summary>
    public async Task<List<object>> PartyRatesAsync(long productId, long? partyId, bool purchase, CancellationToken ct)
    {
        var types = purchase ? new[] { VoucherType.Purchase, VoucherType.LocalPurchase } : new[] { VoucherType.Sales, VoucherType.LocalSales, VoucherType.POSSales };
        var rows = await db.TradeDocumentLines.AsNoTracking()
            .Where(l => l.ProductId == productId)
            .Join(db.TradeDocuments.Where(d => types.Contains(d.VoucherType) && !d.Cancelled && (partyId == null || d.PartyLedgerId == partyId)),
                l => l.DocumentId, d => d.Id, (l, d) => new { d.Date, d.DocumentNo, d.PartyName, l.Qty, l.Rate, l.Mrp, l.DiscountPercent, l.BatchCode })
            .OrderByDescending(x => x.Date).Take(20).ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    public async Task<(byte[] Content, string ContentType)?> ImageAsync(long productId, CancellationToken ct)
    {
        var img = await db.ProductImages.AsNoTracking().Where(i => i.ProductId == productId && db.Products.Any(p => p.Id == productId))
            .OrderBy(i => i.SortOrder).FirstOrDefaultAsync(ct);
        return img is null ? null : (img.Content, img.ContentType);
    }

    public async Task SetImageAsync(long productId, byte[]? content, string? contentType, CancellationToken ct)
    {
        _ = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct) ?? throw new NotFoundException("Product not found.");
        await db.ProductImages.Where(i => i.ProductId == productId).ExecuteDeleteAsync(ct);
        if (content is { Length: > 0 })
        {
            if (content.Length > 2 * 1024 * 1024) throw new DomainException("Image must be 2 MB or smaller.");
            if (contentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) throw new DomainException("Use a PNG, JPEG, WebP or GIF image.");
            db.ProductImages.Add(new ProductImage { ProductId = productId, Content = content, ContentType = contentType });
        }
        await db.SaveChangesAsync(ct);
    }
}
