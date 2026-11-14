using Microsoft.EntityFrameworkCore;
using NasPos.Application.Settings;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Common;

/// <summary>Writes user action log rows into the caller's unit of work (so they commit with the change).</summary>
public class AuditService(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public void Log(string screen, string action, string? remarks = null)
    {
        db.UserActionLogs.Add(new UserActionLog
        {
            CompanyId = user.IsAuthenticated ? user.CompanyId : null,
            UserId = user.IsAuthenticated ? user.UserId : null,
            UserName = user.IsAuthenticated ? user.UserName : "system",
            Screen = screen,
            Action = action,
            Remarks = remarks?.Length > 1000 ? remarks[..1000] : remarks,
            Machine = user.Machine,
            At = clock.Now,
        });
    }
}

/// <summary>Resolves the ids of the company's system ledgers (cached for the request).</summary>
public class SystemLedgerResolver(IAppDbContext db)
{
    private Dictionary<SystemLedger, long>? _map;

    public async Task<Func<SystemLedger, long>> GetAsync(CancellationToken ct = default)
    {
        _map ??= await db.Ledgers.Where(l => l.SystemCode != null)
            .ToDictionaryAsync(l => l.SystemCode!.Value, l => l.Id, ct);
        var map = _map;
        return code => map.TryGetValue(code, out var id)
            ? id
            : throw new DomainException($"System ledger '{code}' is missing for this company.", "missing_system_ledger");
    }
}

public class SettingsService(IAppDbContext db)
{
    private CompanySettings? _cached;

    public async Task<CompanySettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var values = await db.CompanySettings.ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return _cached = new CompanySettings(values);
    }

    public async Task SaveAsync(IDictionary<string, string?> values, CancellationToken ct = default)
    {
        var known = CompanySettings.Definitions.ToDictionary(d => d.Key);
        var existing = await db.CompanySettings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, value) in values)
        {
            if (!known.TryGetValue(key, out var def)) throw new DomainException($"Unknown setting '{key}'.", "unknown_setting");
            if (def.Type == SettingType.Choice && def.Options is not null && !def.Options.Contains(value))
                throw new DomainException($"Invalid value for {def.Label}.", "invalid_setting");
            if (existing.TryGetValue(key, out var row)) row.Value = value;
            else db.CompanySettings.Add(new CompanySetting { Key = key, Value = value });
        }
        _cached = null;
    }
}

/// <summary>
/// Allocates document numbers from <see cref="DocumentSequence"/> with an atomic UPDATE ... OUTPUT inside the caller's
/// transaction. The row lock serialises concurrent counters; there is no MAX(id)+1 read.
/// </summary>
public class NumberingService(IAppDbContext db, ICurrentUser user)
{
    public static readonly IReadOnlyDictionary<VoucherType, string> DefaultPrefixes = new Dictionary<VoucherType, string>
    {
        [VoucherType.Sales] = "S", [VoucherType.LocalSales] = "LS", [VoucherType.Estimate] = "EST",
        [VoucherType.SalesOrder] = "SO", [VoucherType.SalesReturn] = "SR", [VoucherType.DeliveryNote] = "DN",
        [VoucherType.Purchase] = "P", [VoucherType.LocalPurchase] = "LP", [VoucherType.ReceiptNote] = "RN",
        [VoucherType.PurchaseReturn] = "PR", [VoucherType.Receipt] = "RC", [VoucherType.Payment] = "PY",
        [VoucherType.Journal] = "JV", [VoucherType.Contra] = "CT", [VoucherType.DebitNote] = "DBN",
        [VoucherType.CreditNote] = "CRN", [VoucherType.OpeningStock] = "OS", [VoucherType.StockAdjustment] = "SA",
        [VoucherType.Production] = "PRD", [VoucherType.PurchaseOrder] = "PO", [VoucherType.POSSales] = "POS",
        [VoucherType.Opening] = "OB",
    };

    public async Task<(long SequenceNo, string DocumentNo)> NextAsync(VoucherType type, long financialYearId, CancellationToken ct = default)
    {
        long companyId = user.CompanyId;
        int t = (int)type;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var rows = await db.Database.SqlQuery<AllocatedNumber>(
                $"UPDATE DocumentSequences WITH (UPDLOCK, ROWLOCK) SET NextNumber = NextNumber + 1 OUTPUT deleted.NextNumber AS Number, deleted.Prefix AS Prefix WHERE CompanyId = {companyId} AND FinancialYearId = {financialYearId} AND VoucherType = {t}")
                .ToListAsync(ct);
            if (rows.Count == 1) return (rows[0].Number, rows[0].Prefix + rows[0].Number);

            // First document of this type in this year: create the sequence row (unique index guards races).
            string prefix = await db.DocumentSequences.Where(s => s.VoucherType == type)
                .OrderByDescending(s => s.FinancialYearId).Select(s => s.Prefix).FirstOrDefaultAsync(ct)
                ?? DefaultPrefixes.GetValueOrDefault(type, "");
            await db.Database.ExecuteSqlAsync(
                $"IF NOT EXISTS (SELECT 1 FROM DocumentSequences WITH (UPDLOCK, HOLDLOCK) WHERE CompanyId = {companyId} AND FinancialYearId = {financialYearId} AND VoucherType = {t}) INSERT INTO DocumentSequences (CompanyId, FinancialYearId, VoucherType, Prefix, NextNumber) VALUES ({companyId}, {financialYearId}, {t}, {prefix}, 1)", ct);
        }
        throw new DomainException("Could not allocate a document number.", "numbering_failed");
    }

    /// <summary>Preview only (not reserved): the number the next save would get.</summary>
    public async Task<string> PeekAsync(VoucherType type, long financialYearId, CancellationToken ct = default)
    {
        var seq = await db.DocumentSequences.AsNoTracking()
            .FirstOrDefaultAsync(s => s.FinancialYearId == financialYearId && s.VoucherType == type, ct);
        return seq is null ? DefaultPrefixes.GetValueOrDefault(type, "") + "1" : seq.Prefix + seq.NextNumber;
    }

    public sealed class AllocatedNumber
    {
        public long Number { get; set; }
        public string Prefix { get; set; } = "";
    }
}

/// <summary>Resolves the active financial year and guards postings into closed years.</summary>
public class FinancialYearService(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public async Task<FinancialYear> CurrentAsync(CancellationToken ct = default) =>
        await db.FinancialYears.FirstOrDefaultAsync(f => f.Id == user.FinancialYearId, ct)
        ?? throw new DomainException("No financial year selected.", "no_financial_year");

    /// <summary>Returns the open year containing the date, or throws.</summary>
    public async Task<FinancialYear> ForPostingAsync(DateTimeOffset date, CancellationToken ct = default)
    {
        var d = clock.BusinessDate(date);
        var fy = await db.FinancialYears.FirstOrDefaultAsync(f => f.StartDate <= d && f.EndDate >= d, ct)
                 ?? throw new DomainException($"No financial year covers {d:dd-MMM-yyyy}.", "no_financial_year");
        if (fy.IsClosed) throw new DomainException($"Financial year {fy.Name} is closed and read-only.", "year_closed");
        return fy;
    }
}
