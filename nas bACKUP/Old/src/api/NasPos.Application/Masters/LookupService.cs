using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Masters;

public sealed record LookupDto(long Id, string Name, bool IsActive, bool ShowInPos = true, int SortOrder = 0, string? Color = null, int UsageCount = 0);

/// <summary>Shared "master list" for simple lookups: area, category, manufacturer, price list, SMS event.</summary>
public class LookupService(IAppDbContext db, AuditService audit, IRealtimeNotifier notifier, ICurrentUser user)
{
    public static readonly string[] Kinds = ["area", "category", "manufacturer", "pricelist", "smsevent"];

    public async Task<List<LookupDto>> ListAsync(string kind, bool activeOnly, CancellationToken ct) => kind switch
    {
        "area" => await db.Areas.AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new LookupDto(x.Id, x.Name, x.IsActive, true, 0, null, db.Ledgers.Count(l => l.AreaId == x.Id))).ToListAsync(ct),
        "category" => await db.Categories.AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new LookupDto(x.Id, x.Name, x.IsActive, x.ShowInPos, x.SortOrder, x.Color, db.Products.Count(p => p.CategoryId == x.Id))).ToListAsync(ct),
        "manufacturer" => await db.Manufacturers.AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new LookupDto(x.Id, x.Name, x.IsActive, true, 0, null, db.Products.Count(p => p.ManufacturerId == x.Id))).ToListAsync(ct),
        "pricelist" => await db.PriceLists.AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new LookupDto(x.Id, x.Name, x.IsActive, true, 0, null, db.PriceListItems.Count(p => p.PriceListId == x.Id))).ToListAsync(ct),
        "smsevent" => await db.SmsEvents.AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new LookupDto(x.Id, x.Name, x.IsActive, true, 0, null, db.SmsTemplates.Count(t => t.EventId == x.Id))).ToListAsync(ct),
        _ => throw new NotFoundException($"Unknown list '{kind}'."),
    };

    public async Task<long> SaveAsync(string kind, LookupDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new DomainException("Name is required.");
        long id = kind switch
        {
            "area" => await Upsert(db.Areas, dto.Id, name, e => { e.Name = name; e.IsActive = dto.IsActive; }, ct),
            "category" => await Upsert(db.Categories, dto.Id, name, e => { e.Name = name; e.IsActive = dto.IsActive; e.ShowInPos = dto.ShowInPos; e.SortOrder = dto.SortOrder; e.Color = dto.Color; }, ct),
            "manufacturer" => await Upsert(db.Manufacturers, dto.Id, name, e => { e.Name = name; e.IsActive = dto.IsActive; }, ct),
            "pricelist" => await Upsert(db.PriceLists, dto.Id, name, e => { e.Name = name; e.IsActive = dto.IsActive; }, ct),
            "smsevent" => await Upsert(db.SmsEvents, dto.Id, name, e => { e.Name = name; e.IsActive = dto.IsActive; }, ct),
            _ => throw new NotFoundException($"Unknown list '{kind}'."),
        };
        audit.Log(Screens.Masters, dto.Id == 0 ? "Add" : "Edit", $"{kind}: {name}");
        await db.SaveChangesAsync(ct);
        await notifier.MasterChangedAsync(user.CompanyId, kind);
        return id;
    }

    private async Task<long> Upsert<T>(DbSet<T> set, long id, string name, Action<T> apply, CancellationToken ct) where T : TenantEntity, new()
    {
        var existingNames = await set.Select(e => new { e.Id, Name = EF.Property<string>(e, "Name") }).Where(e => e.Name == name && e.Id != id).AnyAsync(ct);
        if (existingNames) throw new DomainException($"'{name}' already exists.", "duplicate");
        T entity;
        if (id > 0) entity = await set.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Record not found.");
        else { entity = new T(); set.Add(entity); }
        apply(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task DeleteAsync(string kind, long id, CancellationToken ct)
    {
        bool inUse = kind switch
        {
            "area" => await db.Ledgers.AnyAsync(l => l.AreaId == id, ct),
            "category" => await db.Products.AnyAsync(p => p.CategoryId == id, ct),
            "manufacturer" => await db.Products.AnyAsync(p => p.ManufacturerId == id, ct),
            "pricelist" => await db.Ledgers.AnyAsync(l => l.PriceListId == id, ct) || await db.TradeDocuments.AnyAsync(d => d.PriceListId == id, ct),
            "smsevent" => await db.SmsTemplates.AnyAsync(t => t.EventId == id, ct),
            _ => throw new NotFoundException($"Unknown list '{kind}'."),
        };
        if (inUse) throw new DomainException("This entry is in use and cannot be deleted. Deactivate it instead.", "in_use");
        switch (kind)
        {
            case "area": db.Areas.Remove(await db.Areas.FirstAsync(x => x.Id == id, ct)); break;
            case "category": db.Categories.Remove(await db.Categories.FirstAsync(x => x.Id == id, ct)); break;
            case "manufacturer": db.Manufacturers.Remove(await db.Manufacturers.FirstAsync(x => x.Id == id, ct)); break;
            case "pricelist":
                await db.PriceListItems.Where(i => i.PriceListId == id).ExecuteDeleteAsync(ct);
                db.PriceLists.Remove(await db.PriceLists.FirstAsync(x => x.Id == id, ct));
                break;
            case "smsevent": db.SmsEvents.Remove(await db.SmsEvents.FirstAsync(x => x.Id == id, ct)); break;
        }
        audit.Log(Screens.Masters, "Delete", $"{kind} #{id}");
        await db.SaveChangesAsync(ct);
        await notifier.MasterChangedAsync(user.CompanyId, kind);
    }
}
