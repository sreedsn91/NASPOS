using Microsoft.EntityFrameworkCore;
using NasPos.Application.Accounting;
using NasPos.Application.Common;
using NasPos.Application.Inventory;
using NasPos.Domain;
using NasPos.Domain.Entities;
using NasPos.Domain.Validation;

namespace NasPos.Application.Masters;

public sealed record AccountGroupDto(long Id, string Name, AccountNature Nature, bool IsDirect, long? ParentId, string? ParentName,
    bool IsSystem, bool IsActive, SystemGroup? SystemCode);

public sealed class LedgerDto
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PrintName { get; set; }
    public string? RegionalName { get; set; }
    public long AccountGroupId { get; set; }
    public string? AccountGroupName { get; set; }
    public PartyType PartyType { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? StateCode { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public long? AreaId { get; set; }
    public long? PriceListId { get; set; }
    public long? LoyaltySchemeId { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal CreditLimit { get; set; }
    public int CreditDays { get; set; }
    public long? SalespersonId { get; set; }
    public bool SmsOptIn { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public SystemLedger? SystemCode { get; set; }
    public bool IsCardLedger { get; set; }
    public bool IsCorporate { get; set; }
    public decimal OpeningBalance { get; set; }
    public EntrySide OpeningSide { get; set; } = EntrySide.Dr;
    public decimal Balance { get; set; }
    public decimal LoyaltyPoints { get; set; }
    public string? RowVersion { get; set; }
}

public sealed record LedgerListItem(long Id, string Code, string Name, string? GroupName, PartyType PartyType, string? Mobile, string? City,
    string? Gstin, decimal Balance, bool IsActive, bool IsSystem);

public sealed record PartyLookup(long Id, string Code, string Name, string? Mobile, string? Address, string? Gstin, string? StateCode,
    long? PriceListId, long? LoyaltySchemeId, decimal DiscountPercent, decimal CreditLimit, long? SalespersonId, decimal Balance,
    decimal LoyaltyPoints, decimal LoyaltyTarget, bool IsWalkIn);

public sealed record OpeningBalanceRow(long LedgerId, string Code, string Name, string? GroupName, decimal Amount, EntrySide Side);

public class AccountGroupService(IAppDbContext db, AuditService audit)
{
    public Task<List<AccountGroupDto>> ListAsync(CancellationToken ct) =>
        db.AccountGroups.AsNoTracking().OrderBy(g => g.Nature).ThenBy(g => g.Name)
            .Select(g => new AccountGroupDto(g.Id, g.Name, g.Nature, g.IsDirect, g.ParentId, g.Parent != null ? g.Parent.Name : null, g.IsSystem, g.IsActive, g.SystemCode))
            .ToListAsync(ct);

    public async Task<long> SaveAsync(AccountGroupDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new DomainException("Group name is required.");
        if (await db.AccountGroups.AnyAsync(g => g.Name == name && g.Id != dto.Id, ct)) throw new DomainException("A group with this name exists.", "duplicate");
        AccountGroup g;
        if (dto.Id > 0)
        {
            g = await db.AccountGroups.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Group not found.");
            if (g.IsSystem && (g.Nature != dto.Nature || g.ParentId != dto.ParentId)) throw new DomainException("The nature and parent of a system group cannot change.");
        }
        else { g = new AccountGroup(); db.AccountGroups.Add(g); }
        if (dto.ParentId is { } pid)
        {
            if (pid == dto.Id) throw new DomainException("A group cannot be its own parent.");
            var parent = await db.AccountGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw new DomainException("Parent group not found.");
            // Walk up to prevent cycles.
            var cursor = parent;
            while (cursor.ParentId is not null)
            {
                if (cursor.ParentId == dto.Id) throw new DomainException("That parent would create a cycle.");
                cursor = await db.AccountGroups.AsNoTracking().FirstAsync(x => x.Id == cursor.ParentId, ct);
            }
            if (parent.Nature != dto.Nature) throw new DomainException("A sub-group must have the same nature as its parent.");
        }
        g.Name = name; g.Nature = dto.Nature; g.IsDirect = dto.IsDirect; g.ParentId = dto.ParentId; g.IsActive = dto.IsActive;
        audit.Log(Screens.AccountGroup, dto.Id == 0 ? "Add" : "Edit", name);
        await db.SaveChangesAsync(ct);
        return g.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var g = await db.AccountGroups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Group not found.");
        if (g.IsSystem) throw new DomainException("System groups cannot be deleted.");
        if (await db.Ledgers.AnyAsync(l => l.AccountGroupId == id, ct) || await db.AccountGroups.AnyAsync(x => x.ParentId == id, ct))
            throw new DomainException("The group has ledgers or sub-groups.", "in_use");
        db.AccountGroups.Remove(g);
        audit.Log(Screens.AccountGroup, "Delete", g.Name);
        await db.SaveChangesAsync(ct);
    }
}

public class LedgerService(IAppDbContext db, ICurrentUser user, AuditService audit, LedgerQueries queries, VoucherService vouchers,
    IRealtimeNotifier notifier, IClock clock)
{
    public async Task<PagedResult<LedgerListItem>> ListAsync(PartyType? partyType, long? groupId, string? search, bool? active, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Ledgers.AsNoTracking().AsQueryable();
        if (partyType is not null) q = q.Where(l => l.PartyType == partyType);
        if (groupId is not null) q = q.Where(l => l.AccountGroupId == groupId);
        if (active is not null) q = q.Where(l => l.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(l => l.Name.Contains(s) || l.Code.Contains(s) || (l.Mobile != null && l.Mobile.Contains(s)) || (l.Gstin != null && l.Gstin.Contains(s)));
        }
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 1000);
        long fy = user.FinancialYearId;
        var rows = await q.OrderBy(l => l.Name).Skip((Math.Max(page, 1) - 1) * size).Take(size)
            .Select(l => new LedgerListItem(l.Id, l.Code, l.Name, l.AccountGroup!.Name, l.PartyType, l.Mobile, l.City, l.Gstin,
                db.AccountEntries.Where(e => e.LedgerId == l.Id && e.FinancialYearId == fy).Sum(e => (decimal?)(e.Debit - e.Credit)) ?? 0,
                l.IsActive, l.IsSystem))
            .ToListAsync(ct);
        return new PagedResult<LedgerListItem>(rows, total, page, size);
    }

    /// <summary>Fast party lookup for billing screens: pulls GSTIN, balance, credit limit, loyalty and price list.</summary>
    public async Task<List<PartyLookup>> PartiesAsync(PartyType type, string? search, int take, CancellationToken ct)
    {
        var q = db.Ledgers.AsNoTracking().Where(l => l.PartyType == type && l.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(l => l.Name.Contains(s) || l.Code == s || (l.Mobile != null && l.Mobile.Contains(s)) || (l.Phone != null && l.Phone.Contains(s)));
        }
        long fy = user.FinancialYearId;
        var rows = await q.OrderBy(l => l.SystemCode == SystemLedger.WalkInCustomer ? 0 : 1).ThenBy(l => l.Name).Take(Math.Clamp(take, 1, 100))
            .Select(l => new PartyLookup(l.Id, l.Code, l.Name, l.Mobile ?? l.Phone, l.Address, l.Gstin, l.StateCode, l.PriceListId, l.LoyaltySchemeId,
                l.DiscountPercent, l.CreditLimit, l.SalespersonId,
                db.AccountEntries.Where(e => e.LedgerId == l.Id && e.FinancialYearId == fy).Sum(e => (decimal?)(e.Debit - e.Credit)) ?? 0,
                db.LoyaltyTransactions.Where(t => t.LedgerId == l.Id && t.FinancialYearId == fy).Sum(t => (decimal?)(t.PointsIn - t.PointsOut)) ?? 0,
                l.LoyaltyScheme != null ? l.LoyaltyScheme.TargetPoints : 0,
                l.SystemCode == SystemLedger.WalkInCustomer))
            .ToListAsync(ct);
        return rows;
    }

    public async Task<LedgerDto> GetAsync(long id, CancellationToken ct)
    {
        var l = await db.Ledgers.AsNoTracking().Include(x => x.AccountGroup).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ledger not found.");
        var opening = await OpeningLineAsync(id, ct);
        return new LedgerDto
        {
            Id = l.Id, Code = l.Code, Name = l.Name, PrintName = l.PrintName, RegionalName = l.RegionalName, AccountGroupId = l.AccountGroupId,
            AccountGroupName = l.AccountGroup?.Name, PartyType = l.PartyType, Address = l.Address, City = l.City, StateCode = l.StateCode,
            Phone = l.Phone, Mobile = l.Mobile, Email = l.Email, Gstin = l.Gstin, AreaId = l.AreaId, PriceListId = l.PriceListId,
            LoyaltySchemeId = l.LoyaltySchemeId, DiscountPercent = l.DiscountPercent, CreditLimit = l.CreditLimit, CreditDays = l.CreditDays,
            SalespersonId = l.SalespersonId, SmsOptIn = l.SmsOptIn, Remarks = l.Remarks, IsActive = l.IsActive, IsSystem = l.IsSystem,
            SystemCode = l.SystemCode, IsCardLedger = l.IsCardLedger, IsCorporate = l.IsCorporate, RowVersion = Convert.ToBase64String(l.RowVersion),
            OpeningBalance = opening?.Amount ?? 0, OpeningSide = opening?.Side ?? EntrySide.Dr,
            Balance = await queries.BalanceAsync(id, null, ct), LoyaltyPoints = await queries.LoyaltyBalanceAsync(id, null, ct),
        };
    }

    public async Task<long> SaveAsync(LedgerDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new DomainException("Name is required.");
        var group = await db.AccountGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == dto.AccountGroupId, ct) ?? throw new DomainException("Select an account group.");
        if (!string.IsNullOrWhiteSpace(dto.Gstin))
        {
            Gstin.EnsureValid(dto.Gstin);
            dto.Gstin = Gstin.Normalize(dto.Gstin);
        }
        if (!string.IsNullOrWhiteSpace(dto.Gstin) && !string.IsNullOrWhiteSpace(dto.StateCode) && Gstin.StateCode(dto.Gstin) != dto.StateCode.Trim())
            throw new DomainException("The state does not match the GSTIN's state code.", "state_mismatch");
        if (!string.IsNullOrWhiteSpace(dto.Email) && !dto.Email.Contains('@')) throw new DomainException("E-mail is not valid.");
        if (dto.CreditLimit < 0 || dto.DiscountPercent is < 0 or > 100) throw new DomainException("Credit limit / discount out of range.");

        Ledger l;
        if (dto.Id > 0)
        {
            l = await db.Ledgers.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Ledger not found.");
            if (!string.IsNullOrEmpty(dto.RowVersion) && !Convert.FromBase64String(dto.RowVersion).SequenceEqual(l.RowVersion))
                throw new ConflictException("This ledger was changed by another user. Reload and try again.");
            if (l.IsSystem && l.AccountGroupId != dto.AccountGroupId) throw new DomainException("The group of a system ledger cannot change.");
            if (l.IsSystem && !dto.IsActive) throw new DomainException("System ledgers cannot be deactivated.");
        }
        else { l = new Ledger(); db.Ledgers.Add(l); }

        string code = string.IsNullOrWhiteSpace(dto.Code) ? await NextCodeAsync(dto.PartyType, ct) : dto.Code.Trim().ToUpperInvariant();
        if (await db.Ledgers.AnyAsync(x => x.Code == code && x.Id != dto.Id, ct)) throw new DomainException($"Code {code} is already used.", "duplicate");
        // Only customers/suppliers can be corporate; for them duplicate names are allowed (branches, sister firms).
        var partyType = group.SystemCode switch
        {
            SystemGroup.SundryDebtors => PartyType.Customer,
            SystemGroup.SundryCreditors => PartyType.Supplier,
            SystemGroup.Employees => PartyType.Employee,
            _ => dto.PartyType,
        };
        bool corporate = dto.IsCorporate && partyType is PartyType.Customer or PartyType.Supplier;
        if (!corporate && await db.Ledgers.AnyAsync(x => x.Name == name && !x.IsCorporate && x.Id != dto.Id, ct))
            throw new DomainException($"A ledger named '{name}' already exists. If this is a company/firm with the same name, mark it as Corporate.", "duplicate");

        l.Code = code; l.Name = name; l.PrintName = dto.PrintName?.Trim(); l.RegionalName = dto.RegionalName?.Trim();
        l.AccountGroupId = group.Id;
        l.PartyType = partyType;
        l.Address = dto.Address?.Trim(); l.City = dto.City?.Trim(); l.StateCode = dto.StateCode?.Trim();
        l.Phone = dto.Phone?.Trim(); l.Mobile = dto.Mobile?.Trim(); l.Email = dto.Email?.Trim();
        l.Gstin = string.IsNullOrWhiteSpace(dto.Gstin) ? null : dto.Gstin.Trim().ToUpperInvariant();
        if (l.Gstin is not null && string.IsNullOrWhiteSpace(l.StateCode)) l.StateCode = Gstin.StateCode(l.Gstin);
        l.AreaId = dto.AreaId; l.PriceListId = dto.PriceListId; l.LoyaltySchemeId = dto.LoyaltySchemeId;
        l.DiscountPercent = dto.DiscountPercent; l.CreditLimit = dto.CreditLimit; l.CreditDays = dto.CreditDays;
        l.SalespersonId = dto.SalespersonId; l.SmsOptIn = dto.SmsOptIn; l.Remarks = dto.Remarks?.Trim(); l.IsActive = dto.IsActive;
        l.IsCardLedger = dto.IsCardLedger;
        l.IsCorporate = corporate;

        await using var tx = await db.BeginAsync(ct);
        audit.Log(ScreenFor(l.PartyType), dto.Id == 0 ? "Add" : "Edit", $"{l.Code} {l.Name}");
        await db.SaveChangesAsync(ct);
        var existing = await OpeningLineAsync(l.Id, ct);
        if ((existing?.Amount ?? 0) != dto.OpeningBalance || (existing is not null && existing.Value.Side != dto.OpeningSide))
            await SetOpeningAsync(l.Id, dto.OpeningBalance, dto.OpeningSide, ct);
        await tx.CommitAsync(ct);
        await notifier.MasterChangedAsync(user.CompanyId, "ledger");
        return l.Id;
    }

    private static string ScreenFor(PartyType t) => t switch
    {
        PartyType.Customer => Screens.Customer,
        PartyType.Supplier => Screens.Supplier,
        PartyType.Employee => Screens.Employee,
        _ => Screens.Ledger,
    };

    public async Task<string> NextCodeAsync(PartyType type, CancellationToken ct)
    {
        string prefix = type switch { PartyType.Customer => "C", PartyType.Supplier => "V", PartyType.Employee => "E", _ => "L" };
        var codes = await db.Ledgers.Where(l => l.Code.StartsWith(prefix)).Select(l => l.Code).ToListAsync(ct);
        int max = codes.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:0000}";
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var l = await db.Ledgers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Ledger not found.");
        if (l.IsSystem) throw new DomainException("System ledgers cannot be deleted.");
        bool used = await db.AccountEntries.AnyAsync(e => e.LedgerId == id || e.ContraLedgerId == id, ct)
                    || await db.TradeDocuments.AnyAsync(d => d.PartyLedgerId == id, ct)
                    || await db.VoucherLines.AnyAsync(v => v.LedgerId == id, ct)
                    || await db.Employees.AnyAsync(e => e.LedgerId == id, ct);
        if (used) throw new DomainException("This ledger is connected with transactions and cannot be deleted. Deactivate it instead.", "in_use");
        db.Ledgers.Remove(l);
        audit.Log(ScreenFor(l.PartyType), "Delete", $"{l.Code} {l.Name}");
        await db.SaveChangesAsync(ct);
    }

    private async Task<(decimal Amount, EntrySide Side, long VoucherId)?> OpeningLineAsync(long ledgerId, CancellationToken ct)
    {
        long fy = user.FinancialYearId;
        var line = await db.VoucherLines.AsNoTracking()
            .Where(x => x.LedgerId == ledgerId && db.Vouchers.Any(v => v.Id == x.VoucherId && v.VoucherType == VoucherType.Opening
                                                                       && v.FinancialYearId == fy && !v.Cancelled))
            .Select(x => new { x.Amount, x.Side, x.VoucherId }).FirstOrDefaultAsync(ct);
        return line is null ? null : (line.Amount, line.Side, line.VoucherId);
    }

    /// <summary>One opening-balance voucher per ledger per year, posted against the Opening Balance ledger.</summary>
    public async Task SetOpeningAsync(long ledgerId, decimal amount, EntrySide side, CancellationToken ct)
    {
        if (amount < 0) throw new DomainException("Opening balance cannot be negative; choose Dr or Cr.");
        var existing = await OpeningLineAsync(ledgerId, ct);
        var fy = await db.FinancialYears.AsNoTracking().FirstAsync(f => f.Id == user.FinancialYearId, ct);
        if (fy.IsClosed) throw new DomainException("The financial year is closed.", "year_closed");
        if (amount == 0)
        {
            if (existing is not null) await vouchers.CancelAsync(existing.Value.VoucherId, ct);
            return;
        }
        var dto = new VoucherDto
        {
            VoucherType = VoucherType.Opening, Date = clock.DayStart(fy.StartDate), Narration = "Opening balance",
            Lines = [new VoucherLineDto { LedgerId = ledgerId, Side = side, Amount = amount }],
        };
        await vouchers.SaveAsync(dto, existing?.VoucherId, ct);
    }

    public async Task<List<OpeningBalanceRow>> OpeningBalancesAsync(CancellationToken ct)
    {
        long fy = user.FinancialYearId;
        var lines = await db.VoucherLines.AsNoTracking()
            .Where(x => db.Vouchers.Any(v => v.Id == x.VoucherId && v.VoucherType == VoucherType.Opening && v.FinancialYearId == fy && !v.Cancelled))
            .Select(x => new { x.LedgerId, x.Amount, x.Side }).ToListAsync(ct);
        var map = lines.GroupBy(x => x.LedgerId).ToDictionary(g => g.Key, g => g.First());
        var ledgers = await db.Ledgers.AsNoTracking().Where(l => l.SystemCode != SystemLedger.OpeningBalance && l.IsActive)
            .OrderBy(l => l.AccountGroup!.Name).ThenBy(l => l.Name)
            .Select(l => new { l.Id, l.Code, l.Name, Group = l.AccountGroup!.Name }).ToListAsync(ct);
        return ledgers.Select(l => new OpeningBalanceRow(l.Id, l.Code, l.Name, l.Group,
            map.TryGetValue(l.Id, out var o) ? o.Amount : 0, map.TryGetValue(l.Id, out var o2) ? o2.Side : EntrySide.Dr)).ToList();
    }
}

public sealed class EmployeeDto
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long? LedgerId { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Gender { get; set; }
    public string? BloodGroup { get; set; }
    public TimeOnly? ShiftStart { get; set; }
    public TimeOnly? ShiftEnd { get; set; }
    public decimal Salary { get; set; }
    public decimal CommissionPercent { get; set; }
    public string? Category { get; set; }
    public bool IsSalesperson { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public class EmployeeService(IAppDbContext db, AuditService audit)
{
    public Task<List<EmployeeDto>> ListAsync(bool activeOnly, CancellationToken ct) =>
        db.Employees.AsNoTracking().Where(e => !activeOnly || e.IsActive).OrderBy(e => e.Name).Select(e => new EmployeeDto
        {
            Id = e.Id, Code = e.Code, Name = e.Name, LedgerId = e.LedgerId, Address = e.Address, Phone = e.Phone, Gender = e.Gender,
            BloodGroup = e.BloodGroup, ShiftStart = e.ShiftStart, ShiftEnd = e.ShiftEnd, Salary = e.Salary,
            CommissionPercent = e.CommissionPercent, Category = e.Category, IsSalesperson = e.IsSalesperson, IsActive = e.IsActive,
        }).ToListAsync(ct);

    /// <summary>Each employee gets a matching ledger under the Employees group so salary/commission can be posted.</summary>
    public async Task<long> SaveAsync(EmployeeDto dto, CancellationToken ct)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new DomainException("Name is required.");
        if (dto.CommissionPercent is < 0 or > 100) throw new DomainException("Commission % must be between 0 and 100.");
        await using var tx = await db.BeginAsync(ct);
        Employee e;
        if (dto.Id > 0) e = await db.Employees.Include(x => x.Ledger).FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Employee not found.");
        else { e = new Employee(); db.Employees.Add(e); }
        string code = string.IsNullOrWhiteSpace(dto.Code) ? $"EMP{await db.Employees.CountAsync(ct) + 1:000}" : dto.Code.Trim().ToUpperInvariant();
        if (await db.Employees.AnyAsync(x => x.Code == code && x.Id != dto.Id, ct)) throw new DomainException($"Code {code} is already used.", "duplicate");
        e.Code = code; e.Name = name; e.Address = dto.Address; e.Phone = dto.Phone; e.Gender = dto.Gender; e.BloodGroup = dto.BloodGroup;
        e.ShiftStart = dto.ShiftStart; e.ShiftEnd = dto.ShiftEnd; e.Salary = dto.Salary; e.CommissionPercent = dto.CommissionPercent;
        e.Category = dto.Category; e.IsSalesperson = dto.IsSalesperson; e.IsActive = dto.IsActive;

        var group = await db.AccountGroups.FirstAsync(g => g.SystemCode == SystemGroup.Employees, ct);
        string ledgerName = $"{name} ({code})";
        if (e.Ledger is null)
        {
            if (await db.Ledgers.AnyAsync(l => l.Name == ledgerName && !l.IsCorporate, ct)) throw new DomainException($"A ledger named '{ledgerName}' already exists.");
            e.Ledger = new Ledger { Code = code, Name = ledgerName, AccountGroupId = group.Id, PartyType = PartyType.Employee, Phone = dto.Phone, Address = dto.Address };
            if (await db.Ledgers.AnyAsync(l => l.Code == code, ct)) e.Ledger.Code = $"E-{code}";
        }
        else
        {
            e.Ledger.Name = ledgerName; e.Ledger.Phone = dto.Phone; e.Ledger.Address = dto.Address; e.Ledger.IsActive = dto.IsActive;
        }
        audit.Log(Screens.Employee, dto.Id == 0 ? "Add" : "Edit", $"{code} {name}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return e.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee not found.");
        if (await db.TradeDocuments.AnyAsync(d => d.SalespersonId == id, ct) || await db.Ledgers.AnyAsync(l => l.SalespersonId == id, ct)
            || await db.Vouchers.AnyAsync(v => v.SalespersonId == id, ct))
            throw new DomainException("This employee is connected with transactions. Deactivate instead.", "in_use");
        var ledgerId = e.LedgerId;
        db.Employees.Remove(e);
        await db.SaveChangesAsync(ct);
        if (ledgerId is not null && !await db.AccountEntries.AnyAsync(x => x.LedgerId == ledgerId, ct))
        {
            db.Ledgers.Remove(await db.Ledgers.FirstAsync(l => l.Id == ledgerId, ct));
        }
        audit.Log(Screens.Employee, "Delete", e.Name);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record LoyaltySchemeDto(long Id, string Name, decimal SaleAmount, decimal Points, decimal TargetPoints, decimal PointValue, bool IsActive);
public sealed record LoyaltyHistoryRow(DateTimeOffset Date, VoucherType VoucherType, string? DocumentNo, decimal SaleAmount, decimal PointsIn, decimal PointsOut);

public class LoyaltyService(IAppDbContext db, ICurrentUser user, AuditService audit)
{
    public Task<List<LoyaltySchemeDto>> ListAsync(CancellationToken ct) =>
        db.LoyaltySchemes.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new LoyaltySchemeDto(x.Id, x.Name, x.SaleAmount, x.Points, x.TargetPoints, x.PointValue, x.IsActive)).ToListAsync(ct);

    public async Task<long> SaveAsync(LoyaltySchemeDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new DomainException("Name is required.");
        if (dto.SaleAmount <= 0 || dto.Points <= 0) throw new DomainException("Sale amount and points must be positive.");
        if (await db.LoyaltySchemes.AnyAsync(x => x.Name == dto.Name.Trim() && x.Id != dto.Id, ct)) throw new DomainException("A scheme with this name exists.");
        var s = dto.Id > 0 ? await db.LoyaltySchemes.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Scheme not found.") : new LoyaltyScheme();
        s.Name = dto.Name.Trim(); s.SaleAmount = dto.SaleAmount; s.Points = dto.Points; s.TargetPoints = dto.TargetPoints;
        s.PointValue = dto.PointValue <= 0 ? 1 : dto.PointValue; s.IsActive = dto.IsActive;
        if (dto.Id == 0) db.LoyaltySchemes.Add(s);
        audit.Log(Screens.Loyalty, dto.Id == 0 ? "Add" : "Edit", s.Name);
        await db.SaveChangesAsync(ct);
        return s.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        if (await db.Ledgers.AnyAsync(l => l.LoyaltySchemeId == id, ct) || await db.LoyaltyTransactions.AnyAsync(t => t.SchemeId == id, ct))
            throw new DomainException("Scheme is in use. Deactivate instead.", "in_use");
        db.LoyaltySchemes.Remove(await db.LoyaltySchemes.FirstAsync(x => x.Id == id, ct));
        audit.Log(Screens.Loyalty, "Delete", $"#{id}");
        await db.SaveChangesAsync(ct);
    }

    public Task<List<LoyaltyHistoryRow>> HistoryAsync(long ledgerId, CancellationToken ct) =>
        db.LoyaltyTransactions.AsNoTracking().Where(t => t.LedgerId == ledgerId && t.FinancialYearId == user.FinancialYearId)
            .OrderBy(t => t.Date).Select(t => new LoyaltyHistoryRow(t.Date, t.VoucherType, t.DocumentNo, t.SaleAmount, t.PointsIn, t.PointsOut))
            .ToListAsync(ct);
}
