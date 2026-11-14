using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using NasPos.Domain.Calculation;
using NasPos.Domain.Entities;

namespace NasPos.Application.Masters;

public sealed record ItemOfferDto(long Id, string Name, DateTimeOffset StartAt, DateTimeOffset EndAt, long ProductId, string? ProductName,
    long? BatchId, string? BatchCode, decimal Mrp, decimal Rate, decimal Discount, decimal NetRate, bool IsActive, bool IsLive);

public sealed record OfferFillRequest(string Name, DateTimeOffset StartAt, DateTimeOffset EndAt, long? CategoryId, decimal DiscountPercent, decimal DiscountAmount);

public class OfferService(IAppDbContext db, IClock clock, AuditService audit)
{
    public async Task<List<ItemOfferDto>> ListAsync(bool liveOnly, CancellationToken ct)
    {
        var now = clock.Now;
        var q = db.ItemOffers.AsNoTracking().AsQueryable();
        if (liveOnly) q = q.Where(o => o.IsActive && o.StartAt <= now && o.EndAt >= now);
        return await q.OrderByDescending(o => o.StartAt)
            .Select(o => new ItemOfferDto(o.Id, o.Name, o.StartAt, o.EndAt, o.ProductId, o.Product!.Name, o.BatchId,
                db.Batches.Where(b => b.Id == o.BatchId).Select(b => b.Code).FirstOrDefault(), o.Mrp, o.Rate, o.Discount, o.NetRate, o.IsActive,
                o.IsActive && o.StartAt <= now && o.EndAt >= now))
            .Take(2000).ToListAsync(ct);
    }

    public async Task SaveAsync(List<ItemOfferDto> offers, CancellationToken ct)
    {
        await using var tx = await db.BeginAsync(ct);
        foreach (var dto in offers)
        {
            if (dto.EndAt < dto.StartAt) throw new DomainException($"{dto.Name}: the offer ends before it starts.", "offer_window");
            if (dto.Discount < 0 || dto.Discount > dto.Rate) throw new DomainException($"{dto.Name}: discount must be between 0 and the rate.");
            var o = dto.Id > 0 ? await db.ItemOffers.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Offer not found.") : new ItemOffer();
            if (!await db.Products.AnyAsync(p => p.Id == dto.ProductId, ct)) throw new DomainException("Product not found.");
            o.Name = string.IsNullOrWhiteSpace(dto.Name) ? "Offer" : dto.Name.Trim();
            o.StartAt = dto.StartAt; o.EndAt = dto.EndAt; o.ProductId = dto.ProductId; o.BatchId = dto.BatchId;
            o.Mrp = dto.Mrp; o.Rate = dto.Rate; o.Discount = dto.Discount; o.NetRate = dto.Rate - dto.Discount; o.IsActive = dto.IsActive;
            if (dto.Id == 0) db.ItemOffers.Add(o);
        }
        audit.Log(Screens.Offer, "Edit", $"{offers.Count} offers saved");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Creates offers for every active item in a category (per-category fill).</summary>
    public async Task<int> FillAsync(OfferFillRequest r, CancellationToken ct)
    {
        if (r.EndAt < r.StartAt) throw new DomainException("The offer ends before it starts.", "offer_window");
        var items = await db.Products.Where(p => p.IsActive && (r.CategoryId == null || p.CategoryId == r.CategoryId)).ToListAsync(ct);
        foreach (var p in items)
        {
            decimal disc = r.DiscountPercent > 0 ? DocumentCalculator.R2(p.SaleRate * r.DiscountPercent / 100) : Math.Min(r.DiscountAmount, p.SaleRate);
            db.ItemOffers.Add(new ItemOffer
            {
                Name = r.Name, StartAt = r.StartAt, EndAt = r.EndAt, ProductId = p.Id, Mrp = p.Mrp, Rate = p.SaleRate, Discount = disc,
                NetRate = p.SaleRate - disc,
            });
        }
        audit.Log(Screens.Offer, "Add", $"{r.Name}: {items.Count} items");
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        db.ItemOffers.Remove(await db.ItemOffers.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Offer not found."));
        audit.Log(Screens.Offer, "Delete", $"#{id}");
        await db.SaveChangesAsync(ct);
    }
}

public sealed record PriceListRow(long ProductId, string Code, string Name, decimal PurchaseRate, decimal SaleRate, decimal Mrp, decimal? Price);
public sealed record PriceListUpdate(long ProductId, decimal? Price);
/// <summary>Bulk set: base = PurchaseRate | SaleRate | Mrp, then ± value or ± percent.</summary>
public sealed record PriceListBulkRequest(string Base, decimal Value, bool IsPercent, long? CategoryId);

public class PriceListService(IAppDbContext db, AuditService audit)
{
    public async Task<List<PriceListRow>> ItemsAsync(long priceListId, long? categoryId, string? search, CancellationToken ct)
    {
        _ = await db.PriceLists.FirstOrDefaultAsync(p => p.Id == priceListId, ct) ?? throw new NotFoundException("Price list not found.");
        var q = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (categoryId is not null) q = q.Where(p => p.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(p => p.Name.Contains(search) || p.Code.Contains(search));
        return await q.OrderBy(p => p.Name)
            .Select(p => new PriceListRow(p.Id, p.Code, p.Name, p.PurchaseRate, p.SaleRate, p.Mrp,
                db.PriceListItems.Where(i => i.PriceListId == priceListId && i.ProductId == p.Id).Select(i => (decimal?)i.Price).FirstOrDefault()))
            .Take(5000).ToListAsync(ct);
    }

    public async Task SaveAsync(long priceListId, List<PriceListUpdate> updates, CancellationToken ct)
    {
        _ = await db.PriceLists.FirstOrDefaultAsync(p => p.Id == priceListId, ct) ?? throw new NotFoundException("Price list not found.");
        var ids = updates.Select(u => u.ProductId).ToList();
        var existing = await db.PriceListItems.Where(i => i.PriceListId == priceListId && ids.Contains(i.ProductId)).ToDictionaryAsync(i => i.ProductId, ct);
        foreach (var u in updates)
        {
            if (u.Price is < 0) throw new DomainException("Price cannot be negative.");
            if (u.Price is null or 0) { if (existing.TryGetValue(u.ProductId, out var e)) db.PriceListItems.Remove(e); continue; }
            if (existing.TryGetValue(u.ProductId, out var row)) row.Price = u.Price.Value;
            else db.PriceListItems.Add(new PriceListItem { PriceListId = priceListId, ProductId = u.ProductId, Price = u.Price.Value });
        }
        audit.Log(Screens.PriceList, "Edit", $"Price list #{priceListId}: {updates.Count} items");
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> BulkAsync(long priceListId, PriceListBulkRequest r, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive && (r.CategoryId == null || p.CategoryId == r.CategoryId)).ToListAsync(ct);
        var updates = products.Select(p =>
        {
            decimal b = r.Base switch { "PurchaseRate" => p.PurchaseRate, "Mrp" => p.Mrp, _ => p.SaleRate };
            decimal price = r.IsPercent ? b + b * r.Value / 100 : b + r.Value;
            return new PriceListUpdate(p.Id, Math.Max(0, DocumentCalculator.R2(price)));
        }).ToList();
        await SaveAsync(priceListId, updates, ct);
        return updates.Count;
    }
}

public sealed class BomLineDto
{
    public long ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? Unit { get; set; }
    public BomLineKind Kind { get; set; }
    public decimal Qty { get; set; }
    public bool IsAltUnit { get; set; }
}

public sealed class BomDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? FinishedProductId { get; set; }
    public decimal BatchSize { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public List<BomLineDto> Lines { get; set; } = [];
}

public class BomService(IAppDbContext db, AuditService audit)
{
    public Task<List<BomDto>> ListAsync(CancellationToken ct) =>
        db.Boms.AsNoTracking().OrderBy(b => b.Name).Select(b => new BomDto
        {
            Id = b.Id, Name = b.Name, Description = b.Description, FinishedProductId = b.FinishedProductId, BatchSize = b.BatchSize, IsActive = b.IsActive,
        }).ToListAsync(ct);

    public async Task<BomDto> GetAsync(long id, CancellationToken ct)
    {
        var b = await db.Boms.AsNoTracking().Include(x => x.Lines).ThenInclude(l => l.Product).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("BOM not found.");
        return new BomDto
        {
            Id = b.Id, Name = b.Name, Description = b.Description, FinishedProductId = b.FinishedProductId, BatchSize = b.BatchSize, IsActive = b.IsActive,
            Lines = b.Lines.OrderBy(l => l.LineNo).Select(l => new BomLineDto
            {
                ProductId = l.ProductId, ProductName = l.Product?.Name, Kind = l.Kind, Qty = l.Qty, IsAltUnit = l.IsAltUnit,
                Unit = l.IsAltUnit ? l.Product?.AltUnit : l.Product?.MainUnit,
            }).ToList(),
        };
    }

    public async Task<long> SaveAsync(BomDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new DomainException("BOM name is required.");
        if (dto.BatchSize <= 0) throw new DomainException("Batch size must be positive.");
        var lines = dto.Lines.Where(l => l.ProductId > 0 && l.Qty > 0).ToList();
        if (!lines.Any(l => l.Kind == BomLineKind.RawMaterial)) throw new DomainException("Add at least one raw material.");
        if (!lines.Any(l => l.Kind == BomLineKind.FinishedGood) && dto.FinishedProductId is null) throw new DomainException("Add the finished item.");
        if (await db.Boms.AnyAsync(b => b.Name == dto.Name.Trim() && b.Id != dto.Id, ct)) throw new DomainException("A BOM with this name exists.");

        await using var tx = await db.BeginAsync(ct);
        Bom b;
        if (dto.Id > 0)
        {
            b = await db.Boms.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("BOM not found.");
            db.BomLines.RemoveRange(b.Lines);
            b.Lines.Clear();
        }
        else { b = new Bom(); db.Boms.Add(b); }
        b.Name = dto.Name.Trim(); b.Description = dto.Description; b.BatchSize = dto.BatchSize; b.IsActive = dto.IsActive;
        b.FinishedProductId = dto.FinishedProductId ?? lines.First(l => l.Kind == BomLineKind.FinishedGood).ProductId;
        if (!lines.Any(l => l.Kind == BomLineKind.FinishedGood))
            lines.Add(new BomLineDto { ProductId = b.FinishedProductId.Value, Kind = BomLineKind.FinishedGood, Qty = dto.BatchSize });
        int n = 1;
        foreach (var l in lines) b.Lines.Add(new BomLine { LineNo = n++, ProductId = l.ProductId, Kind = l.Kind, Qty = l.Qty, IsAltUnit = l.IsAltUnit });
        audit.Log(Screens.Bom, dto.Id == 0 ? "Add" : "Edit", b.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return b.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        if (await db.TradeDocuments.AnyAsync(d => d.BomId == id, ct)) throw new DomainException("This BOM was used in production. Deactivate instead.", "in_use");
        db.Boms.Remove(await db.Boms.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException("BOM not found."));
        audit.Log(Screens.Bom, "Delete", $"#{id}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Scales a BOM to a finished quantity for the production screen.</summary>
    public async Task<List<BomLineDto>> ScaleAsync(long id, decimal finishedQty, CancellationToken ct)
    {
        var bom = await GetAsync(id, ct);
        decimal factor = bom.BatchSize == 0 ? 1 : finishedQty / bom.BatchSize;
        return bom.Lines.Select(l => new BomLineDto
        {
            ProductId = l.ProductId, ProductName = l.ProductName, Unit = l.Unit, Kind = l.Kind, IsAltUnit = l.IsAltUnit,
            Qty = Math.Round(l.Qty * factor, 4, MidpointRounding.AwayFromZero),
        }).ToList();
    }
}
