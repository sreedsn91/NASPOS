using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Temple;

public sealed record DeityDto(long Id, string Name, string? RegionalName, int SortOrder, bool IsActive);
public sealed record PoojaSlotDto(long Id, string Name, string? RegionalName, TimeOnly? Time, int SortOrder, bool IsActive);
public sealed record DevoteeDto(long Id, string Name, string? RegionalName, int? Star, string? StarName, string? HouseName, string? Phone,
    string? Address, string? City, string? Email, DateOnly? DateOfBirth, string? Notes, bool IsActive);
public sealed record OfferingDto(long Id, string Code, string Name, string? RegionalName, decimal Rate, long? DeityId, string? DeityName,
    long? PoojaSlotId, string? PoojaSlotName, bool NeedsName, bool NeedsStar, long? CategoryId);

/// <summary>Deities, pooja times, devotees and the list of offerings for the counter.</summary>
public class TempleMastersService(IAppDbContext db, AuditService audit)
{
    // ---------------- deities ----------------

    public Task<List<DeityDto>> DeitiesAsync(bool activeOnly, CancellationToken ct) =>
        db.Deities.AsNoTracking().Where(d => !activeOnly || d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new DeityDto(d.Id, d.Name, d.RegionalName, d.SortOrder, d.IsActive)).ToListAsync(ct);

    public async Task<long> SaveDeityAsync(DeityDto dto, CancellationToken ct)
    {
        var name = Required(dto.Name, "deity", 100);
        if (await db.Deities.AnyAsync(d => d.Name == name && d.Id != dto.Id, ct)) throw new DomainException("A deity with this name exists.", "duplicate");
        var d = dto.Id > 0 ? await db.Deities.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Deity not found.") : db.Deities.Add(new Deity()).Entity;
        d.Name = name; d.RegionalName = Trim(dto.RegionalName, 100); d.SortOrder = dto.SortOrder; d.IsActive = dto.IsActive;
        audit.Log(Screens.TempleMasters, dto.Id == 0 ? "Add" : "Edit", $"Deity {name}");
        await db.SaveChangesAsync(ct);
        return d.Id;
    }

    // ---------------- pooja times ----------------

    public Task<List<PoojaSlotDto>> PoojaSlotsAsync(bool activeOnly, CancellationToken ct) =>
        db.PoojaSlots.AsNoTracking().Where(s => !activeOnly || s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Time)
            .Select(s => new PoojaSlotDto(s.Id, s.Name, s.RegionalName, s.Time, s.SortOrder, s.IsActive)).ToListAsync(ct);

    public async Task<long> SavePoojaSlotAsync(PoojaSlotDto dto, CancellationToken ct)
    {
        var name = Required(dto.Name, "pooja time", 100);
        if (await db.PoojaSlots.AnyAsync(s => s.Name == name && s.Id != dto.Id, ct)) throw new DomainException("A pooja time with this name exists.", "duplicate");
        var s = dto.Id > 0 ? await db.PoojaSlots.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Pooja time not found.") : db.PoojaSlots.Add(new PoojaSlot()).Entity;
        s.Name = name; s.RegionalName = Trim(dto.RegionalName, 100); s.Time = dto.Time; s.SortOrder = dto.SortOrder; s.IsActive = dto.IsActive;
        audit.Log(Screens.TempleMasters, dto.Id == 0 ? "Add" : "Edit", $"Pooja time {name}");
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    // ---------------- offerings for the counter ----------------

    public async Task<List<OfferingDto>> OfferingsAsync(CancellationToken ct)
    {
        var deities = await db.Deities.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var slots = await db.PoojaSlots.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var list = await db.Products.AsNoTracking().Where(p => p.IsOffering && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        return list.Select(p => new OfferingDto(p.Id, p.Code, p.Name, p.RegionalName, p.SaleRate, p.DeityId,
            p.DeityId is { } d ? deities.GetValueOrDefault(d) : null, p.PoojaSlotId, p.PoojaSlotId is { } s ? slots.GetValueOrDefault(s) : null,
            p.NeedsName, p.NeedsStar, p.CategoryId)).ToList();
    }

    // ---------------- devotees ----------------

    public async Task<PagedResult<DevoteeDto>> DevoteesAsync(string? search, int? star, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Devotees.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d => d.Name.Contains(s) || (d.Phone != null && d.Phone.Contains(s)) || (d.HouseName != null && d.HouseName.Contains(s))
                             || (d.RegionalName != null && d.RegionalName.Contains(s)));
        }
        if (star is not null) q = q.Where(d => d.Star == star);
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 500);
        var rows = await q.OrderBy(d => d.Name).Skip((Math.Max(page, 1) - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DevoteeDto>(rows.Select(ToDto).ToList(), total, page, size);
    }

    public async Task<DevoteeDto> DevoteeAsync(long id, CancellationToken ct) =>
        ToDto(await db.Devotees.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Devotee not found."));

    public async Task<long> SaveDevoteeAsync(DevoteeDto dto, CancellationToken ct)
    {
        var name = Required(dto.Name, "devotee", 150);
        if (dto.Star is not null and not (>= 0 and < 27)) throw new DomainException("Choose a valid star.");
        var d = dto.Id > 0 ? await db.Devotees.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Devotee not found.") : db.Devotees.Add(new Devotee()).Entity;
        d.Name = name; d.RegionalName = Trim(dto.RegionalName, 150); d.Star = dto.Star; d.HouseName = Trim(dto.HouseName, 150);
        d.Phone = Trim(dto.Phone, 30); d.Address = Trim(dto.Address, 500); d.City = Trim(dto.City, 100); d.Email = Trim(dto.Email, 200);
        d.DateOfBirth = dto.DateOfBirth; d.Notes = Trim(dto.Notes, 1000); d.IsActive = dto.IsActive;
        audit.Log(Screens.Devotee, dto.Id == 0 ? "Add" : "Edit", name);
        await db.SaveChangesAsync(ct);
        return d.Id;
    }

    public static DevoteeDto ToDto(Devotee d) => new(d.Id, d.Name, d.RegionalName, d.Star, d.Star is { } s ? MalayalamNames.Star(s) : null,
        d.HouseName, d.Phone, d.Address, d.City, d.Email, d.DateOfBirth, d.Notes, d.IsActive);

    private static string Required(string? value, string what, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new DomainException($"Enter the {what} name.");
        if (v.Length > max) throw new DomainException($"The {what} name is too long (max {max}).");
        return v;
    }

    private static string? Trim(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().Length > max ? v.Trim()[..max] : v.Trim();
}
