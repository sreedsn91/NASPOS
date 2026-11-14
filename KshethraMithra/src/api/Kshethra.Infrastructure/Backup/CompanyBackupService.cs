using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Kshethra.Application.Common;
using Kshethra.Application.Licensing;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Licensing;
using Kshethra.Infrastructure.Persistence;

namespace Kshethra.Infrastructure.Backup;

/// <summary>
/// Logical backup of one company as a zip of JSON tables, and restore into a NEW company with every id remapped through
/// the EF model's foreign keys. No server-level backup/restore, no sysadmin rights, no raw SQL.
/// </summary>
public sealed class CompanyBackupService(AppDbContext db, ICurrentUser user, AuditService audit) : ICompanyBackupService
{
    private const int FormatVersion = 1;

    /// <summary>Tables in dependency order (parents first). Self/cyclic references are fixed up after insert.</summary>
    private static readonly Type[] Order =
    [
        typeof(FinancialYear), typeof(CompanySetting), typeof(InvoiceSetting), typeof(PrintScheme), typeof(LabelTemplate), typeof(DocumentSequence),
        typeof(SmsEvent), typeof(SmsTemplate), typeof(AccountGroup), typeof(Area), typeof(Category), typeof(Manufacturer), typeof(PriceList),
        typeof(LoyaltyScheme), typeof(Ledger), typeof(Employee), typeof(SmsSchedule), typeof(Product), typeof(ProductImage), typeof(PriceListItem),
        typeof(Batch), typeof(ItemOffer), typeof(Bom), typeof(BomLine), typeof(TradeDocument), typeof(TradeDocumentLine), typeof(Voucher),
        typeof(VoucherLine), typeof(AccountEntry), typeof(StockMovement), typeof(BillOutstanding), typeof(BillSettlement),
        typeof(LoyaltyTransaction), typeof(VoidItem), typeof(FaqEntry),
    ];

    /// <summary>Principals that are not part of a company backup and keep their ids.</summary>
    private static readonly HashSet<Type> Global = [typeof(User), typeof(Role)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task<byte[]> ExportAsync(CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            await WriteAsync(zip, "manifest.json", new { FormatVersion, Company = company.Name, company.StateCode, CreatedAt = DateTimeOffset.UtcNow, Tables = Order.Select(t => t.Name) }, ct);
            await WriteAsync(zip, "Company.json", company, ct);
            foreach (var type in Order)
            {
                var rows = await LoadAsync(type, ct);
                await WriteAsync(zip, type.Name + ".json", rows, ct);
            }
        }
        audit.Log(Screens.Company, "Backup", company.Name);
        await db.SaveChangesAsync(ct);
        return ms.ToArray();
    }

    private static async Task WriteAsync(ZipArchive zip, string name, object value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await JsonSerializer.SerializeAsync(s, value, value.GetType(), Json, ct);
    }

    /// <summary>Rows of one table belonging to the current company (child tables via their parent).</summary>
    private Task<IList> LoadAsync(Type type, CancellationToken ct)
    {
        IQueryable query = type switch
        {
            _ when type == typeof(ProductImage) => db.ProductImages.AsNoTracking().Where(i => db.Products.Any(p => p.Id == i.ProductId)),
            _ when type == typeof(BomLine) => db.BomLines.AsNoTracking().Where(l => db.Boms.Any(b => b.Id == l.BomId)),
            _ when type == typeof(TradeDocumentLine) => db.TradeDocumentLines.AsNoTracking().Where(l => db.TradeDocuments.Any(d => d.Id == l.DocumentId)),
            _ when type == typeof(VoucherLine) => db.VoucherLines.AsNoTracking().Where(l => db.Vouchers.Any(v => v.Id == l.VoucherId)),
            _ when type == typeof(BillSettlement) => db.BillSettlements.AsNoTracking().Where(s => db.BillOutstandings.Any(b => b.Id == s.BillId)),
            _ => (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(type).Invoke(db, null)!,
        };
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        var asNoTracking = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!.MakeGenericMethod(type);
        var q = (IQueryable)asNoTracking.Invoke(null, [query])!;
        foreach (var item in q) { ct.ThrowIfCancellationRequested(); list.Add(item); }
        return Task.FromResult(list);
    }

    private sealed record Deferred(object Entity, IProperty Property, Type Principal, long OldValue);

    private static bool LicenseStillValidFor(Company c)
    {
        try { LicenseKey.Verify(c.LicenseKey!, c.Name, c.Gstin, LicenseTrust.Trusted); return true; }
        catch (LicenseKeyException) { return false; }
    }

    public async Task<long> RestoreAsync(Stream zipStream, string newCompanyName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newCompanyName)) throw new DomainException("Enter a name for the restored temple.");
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var manifest = await ReadAsync<JsonElement>(zip, "manifest.json", ct);
        if (manifest.GetProperty("formatVersion").GetInt32() != FormatVersion) throw new DomainException("Unsupported backup format.");

        using var _ = db.IgnoreTenant();
        if (await db.Companies.AnyAsync(c => c.Name == newCompanyName.Trim(), ct)) throw new DomainException("A temple with this name exists.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var source = await ReadAsync<Company>(zip, "Company.json", ct);
        source.Id = 0;
        source.Name = newCompanyName.Trim();
        source.RowVersion = [];
        // The license belongs to a company name: it carries over only when restored under that same name
        // (e.g. recovering on a new PC). A copy under another name needs its own key.
        source.LicenseGraceUntil = null;
        if (source.LicenseKey is not null && !LicenseStillValidFor(source))
        {
            source.LicenseKey = null; source.LicenseKeyId = null; source.LicenseMonths = null;
            source.LicenseExpiresOn = null; source.LicenseAppliedAt = null;
        }
        db.Companies.Add(source);
        await db.SaveChangesAsync(ct);
        long newCompanyId = source.Id;

        var maps = new Dictionary<Type, Dictionary<long, long>> { [typeof(Company)] = new() };
        var deferred = new List<Deferred>();
        foreach (var type in Order)
        {
            var listType = typeof(List<>).MakeGenericType(type);
            var entry = zip.GetEntry(type.Name + ".json");
            var rows = entry is null ? (IList)Activator.CreateInstance(listType)! : (IList)(await ReadAsync(zip, entry, listType, ct))!;
            var et = db.Model.FindEntityType(type)!;
            var map = maps[type] = new Dictionary<long, long>();
            var pending = new List<(long OldId, Entity E)>();
            foreach (Entity e in rows)
            {
                long oldId = e.Id;
                e.Id = 0;
                if (e is ITenantEntity t) t.CompanyId = newCompanyId;
                ClearNavigations(e, et);
                foreach (var fk in et.GetForeignKeys())
                {
                    var principal = fk.PrincipalEntityType.ClrType;
                    if (principal == typeof(Company) || Global.Contains(principal)) continue;
                    foreach (var prop in fk.Properties)
                    {
                        var raw = prop.PropertyInfo!.GetValue(e);
                        if (raw is null) continue;
                        long old = Convert.ToInt64(raw);
                        if (maps.TryGetValue(principal, out var pm) && pm.TryGetValue(old, out var mapped)) prop.PropertyInfo.SetValue(e, mapped);
                        else if (prop.IsNullable) { prop.PropertyInfo.SetValue(e, null); deferred.Add(new Deferred(e, prop, principal, old)); }
                        else throw new DomainException($"Backup is inconsistent: {type.Name} references missing {principal.Name} {old}.");
                    }
                }
                ResetRowVersion(e);
                db.Add(e);
                pending.Add((oldId, e));
            }
            await db.SaveChangesAsync(ct);
            foreach (var (oldId, e) in pending) map[oldId] = e.Id;
            db.ChangeTracker.Clear();
        }

        foreach (var d in deferred)
        {
            if (!maps.TryGetValue(d.Principal, out var pm) || !pm.TryGetValue(d.OldValue, out var mapped)) continue;
            var e = (Entity)d.Entity;
            db.Attach(e);
            d.Property.PropertyInfo!.SetValue(e, mapped);
            db.Entry(e).Property(d.Property.Name).IsModified = true;
        }
        await db.SaveChangesAsync(ct);

        var adminRole = await db.Roles.FirstAsync(r => r.IsAdmin, ct);
        db.UserCompanies.Add(new UserCompany { UserId = user.UserId, CompanyId = newCompanyId, RoleId = adminRole.Id });
        audit.Log(Screens.Company, "Restore", $"{manifest.GetProperty("company").GetString()} restored as {source.Name}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return newCompanyId;
    }

    private static void ClearNavigations(object e, IEntityType et)
    {
        foreach (var nav in et.GetNavigations())
        {
            var pi = nav.PropertyInfo;
            if (pi is null || !pi.CanWrite) continue;
            if (nav.IsCollection) pi.SetValue(e, Activator.CreateInstance(pi.PropertyType.IsInterface ? typeof(List<>).MakeGenericType(nav.TargetEntityType.ClrType) : pi.PropertyType));
            else pi.SetValue(e, null);
        }
    }

    private static void ResetRowVersion(object e)
    {
        var pi = e.GetType().GetProperty("RowVersion", BindingFlags.Public | BindingFlags.Instance);
        if (pi?.PropertyType == typeof(byte[])) pi.SetValue(e, Array.Empty<byte>());
    }

    private static async Task<T> ReadAsync<T>(ZipArchive zip, string name, CancellationToken ct)
    {
        var entry = zip.GetEntry(name) ?? throw new DomainException($"Backup is missing {name}.");
        return (T)(await ReadAsync(zip, entry, typeof(T), ct))!;
    }

    private static async Task<object?> ReadAsync(ZipArchive zip, ZipArchiveEntry entry, Type type, CancellationToken ct)
    {
        await using var s = entry.Open();
        return await JsonSerializer.DeserializeAsync(s, type, Json, ct);
    }
}
