using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Auth;
using NasPos.Application.Common;
using NasPos.Application.Licensing;
using NasPos.Application.Settings;
using NasPos.Application.Setup;
using NasPos.Domain;
using NasPos.Domain.Entities;
using NasPos.Domain.Validation;

namespace NasPos.Application.Admin;

public sealed class CompanyDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? LegalName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string StateCode { get; set; } = "";
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public string? Pan { get; set; }
    public string? Website { get; set; }
    public string? InvoiceFooter { get; set; }
    public bool HasLogo { get; set; }
    public bool IsActive { get; set; }
    public bool IsHidden { get; set; }
    public DateOnly? FinancialYearStart { get; set; }
    /// <summary>Subscription key, required when creating a company or changing its name/GSTIN away from what the current
    /// key was issued for (input only; never returned).</summary>
    public string? LicenseKey { get; set; }
}

public sealed record FinancialYearDto(long Id, string Name, DateOnly StartDate, DateOnly EndDate, bool IsClosed, DateTimeOffset? ClosedAt);
public sealed record SequenceDto(long Id, VoucherType VoucherType, string Name, string Prefix, long NextNumber);
public sealed record SettingsPayload(IReadOnlyList<SettingDefinition> Definitions, IReadOnlyDictionary<string, string> Values);

public class CompanyService(IAppDbContext db, ICurrentUser user, AuditService audit, SettingsService settings, CompanySetupService setup, IClock clock, LicenseService license)
{
    public async Task<CompanyDto> CurrentAsync(CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().FirstAsync(x => x.Id == user.CompanyId, ct);
        return Map(c);
    }

    private static CompanyDto Map(Company c) => new()
    {
        Id = c.Id, Name = c.Name, LegalName = c.LegalName, Address1 = c.Address1, Address2 = c.Address2, City = c.City, State = c.State,
        StateCode = c.StateCode, Country = c.Country, PostalCode = c.PostalCode, Phone = c.Phone, Email = c.Email, Gstin = c.Gstin, Pan = c.Pan,
        Website = c.Website, InvoiceFooter = c.InvoiceFooter, HasLogo = c.Logo is { Length: > 0 }, IsActive = c.IsActive, IsHidden = c.IsHidden,
    };

    private static void Validate(CompanyDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new DomainException("Company name is required.");
        if (string.IsNullOrWhiteSpace(dto.StateCode) || !IndianStates.Names.ContainsKey(dto.StateCode.Trim())) throw new DomainException("Select the company's state.");
        if (!string.IsNullOrWhiteSpace(dto.Gstin))
        {
            Gstin.EnsureValid(dto.Gstin);
            if (Gstin.StateCode(dto.Gstin) != dto.StateCode.Trim())
                throw new DomainException($"The GSTIN belongs to state {Gstin.StateCode(dto.Gstin)}, but state {dto.StateCode} is selected.", "state_mismatch");
            dto.Gstin = Gstin.Normalize(dto.Gstin);
        }
    }

    public async Task UpdateCurrentAsync(CompanyDto dto, CancellationToken ct)
    {
        Validate(dto);
        var c = await db.Companies.FirstAsync(x => x.Id == user.CompanyId, ct);
        if (await db.Companies.AnyAsync(x => x.Name == dto.Name.Trim() && x.Id != c.Id, ct)) throw new DomainException("Another company has this name.");
        // The license key is checked against the name/GSTIN on every use: a change that would stop a working key needs a
        // new key issued for the new details, entered with the change.
        var newGstin = dto.Gstin?.Trim().ToUpperInvariant();
        if (LicenseService.StoredKey(c) is not null && LicenseService.StoredKey(c.LicenseKey, dto.Name.Trim(), newGstin) is null)
        {
            if (string.IsNullOrWhiteSpace(dto.LicenseKey))
                throw new DomainException("The license key was issued for the current company name and GSTIN (capitals, spaces and punctuation don't matter). " +
                    "To change them, contact NAS POS support for a key with the new details and enter it with this change.", "license_company_mismatch");
            await license.ReplaceKeyAsync(c, dto.LicenseKey, dto.Name.Trim(), newGstin, clock.Today(), ct);
        }
        c.Name = dto.Name.Trim(); c.LegalName = dto.LegalName; c.Address1 = dto.Address1; c.Address2 = dto.Address2; c.City = dto.City;
        c.State = IndianStates.Names.GetValueOrDefault(dto.StateCode.Trim()) ?? dto.State; c.StateCode = dto.StateCode.Trim(); c.Country = dto.Country;
        c.PostalCode = dto.PostalCode; c.Phone = dto.Phone; c.Email = dto.Email; c.Gstin = newGstin; c.Pan = dto.Pan;
        c.Website = dto.Website; c.InvoiceFooter = dto.InvoiceFooter;
        audit.Log(Screens.Company, "Edit", c.Name);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetLogoAsync(byte[]? logo, CancellationToken ct)
    {
        if (logo is { Length: > 1024 * 1024 }) throw new DomainException("Logo must be 1 MB or smaller.");
        var c = await db.Companies.FirstAsync(x => x.Id == user.CompanyId, ct);
        c.Logo = logo;
        await db.SaveChangesAsync(ct);
    }

    public async Task<byte[]?> LogoAsync(CancellationToken ct) =>
        await db.Companies.AsNoTracking().Where(x => x.Id == user.CompanyId).Select(x => x.Logo).FirstOrDefaultAsync(ct);

    /// <summary>Companies the signed-in user belongs to.</summary>
    public async Task<List<CompanyDto>> MineAsync(CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var ids = await db.UserCompanies.Where(m => m.UserId == user.UserId).Select(m => m.CompanyId).ToListAsync(ct);
        return (await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync(ct)).Select(Map).ToList();
    }

    /// <summary>Creates a new company; the creator becomes its administrator.</summary>
    public async Task<long> CreateAsync(CompanyDto dto, CancellationToken ct)
    {
        Validate(dto);
        using var _ = db.IgnoreTenant();
        if (await db.Companies.AnyAsync(x => x.Name == dto.Name.Trim(), ct)) throw new DomainException("A company with this name exists.");
        await using var tx = await db.BeginAsync(ct);
        var c = new Company
        {
            Name = dto.Name.Trim(), LegalName = dto.LegalName ?? dto.Name.Trim(), Address1 = dto.Address1, Address2 = dto.Address2, City = dto.City,
            StateCode = dto.StateCode.Trim(), State = IndianStates.Names.GetValueOrDefault(dto.StateCode.Trim()), PostalCode = dto.PostalCode,
            Phone = dto.Phone, Email = dto.Email, Gstin = dto.Gstin?.Trim().ToUpperInvariant(), Pan = dto.Pan,
        };
        // Every company needs its own subscription key, issued for its name (and GSTIN, if the key carries one).
        LicenseService.Apply(c, LicenseService.Check(dto.LicenseKey, c.Name, c.Gstin, clock.Today()), dto.LicenseKey!, clock.Now);
        db.Companies.Add(c);
        await db.SaveChangesAsync(ct);
        await setup.SeedAsync(c, dto.FinancialYearStart ?? CompanySetupService.FinancialYearFor(clock.Today()).Start, ct);
        var adminRole = await db.Roles.FirstAsync(r => r.IsAdmin, ct);
        db.UserCompanies.Add(new UserCompany { UserId = user.UserId, CompanyId = c.Id, RoleId = adminRole.Id });
        audit.Log(Screens.Company, "Add", c.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return c.Id;
    }

    public async Task SetVisibilityAsync(long companyId, bool hidden, bool active, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        if (!await db.UserCompanies.AnyAsync(m => m.UserId == user.UserId && m.CompanyId == companyId && m.Role!.IsAdmin, ct))
            throw new ForbiddenException("Only an administrator of that company can change it.");
        if (companyId == user.CompanyId && !active) throw new DomainException("You cannot deactivate the company you are signed in to.");
        var c = await db.Companies.FirstAsync(x => x.Id == companyId, ct);
        c.IsHidden = hidden; c.IsActive = active;
        audit.Log(Screens.Company, "Edit", $"{c.Name}: hidden={hidden} active={active}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<SettingsPayload> SettingsAsync(CancellationToken ct) =>
        new(CompanySettings.Definitions, (await settings.GetAsync(ct)).ToDictionary());

    public async Task SaveSettingsAsync(Dictionary<string, string?> values, CancellationToken ct)
    {
        await settings.SaveAsync(values, ct);
        audit.Log(Screens.Settings, "Edit", string.Join(", ", values.Keys));
        await db.SaveChangesAsync(ct);
    }

    public async Task<InvoiceOptions> InvoiceOptionsAsync(VoucherType type, CancellationToken ct)
    {
        var row = await db.InvoiceSettings.AsNoTracking().FirstOrDefaultAsync(x => x.VoucherType == type, ct);
        return row is null ? InvoiceOptions.Default(type) : JsonSerializer.Deserialize<InvoiceOptions>(row.OptionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? InvoiceOptions.Default(type);
    }

    public async Task SaveInvoiceOptionsAsync(VoucherType type, InvoiceOptions options, CancellationToken ct)
    {
        var row = await db.InvoiceSettings.FirstOrDefaultAsync(x => x.VoucherType == type, ct);
        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (row is null) db.InvoiceSettings.Add(new InvoiceSetting { VoucherType = type, OptionsJson = json });
        else row.OptionsJson = json;
        audit.Log(Screens.Settings, "Edit", $"Screen options {type}");
        await db.SaveChangesAsync(ct);
    }

    public Task<List<FinancialYearDto>> YearsAsync(CancellationToken ct) =>
        db.FinancialYears.AsNoTracking().OrderByDescending(f => f.StartDate)
            .Select(f => new FinancialYearDto(f.Id, f.Name, f.StartDate, f.EndDate, f.IsClosed, f.ClosedAt)).ToListAsync(ct);

    public async Task<List<SequenceDto>> SequencesAsync(CancellationToken ct)
    {
        var rows = await db.DocumentSequences.AsNoTracking().Where(s => s.FinancialYearId == user.FinancialYearId).ToListAsync(ct);
        foreach (var (type, prefix) in NumberingService.DefaultPrefixes.Where(p => rows.All(r => r.VoucherType != p.Key)))
            rows.Add(new DocumentSequence { VoucherType = type, Prefix = prefix, NextNumber = 1 });
        return rows.OrderBy(r => r.VoucherType).Select(r => new SequenceDto(r.Id, r.VoucherType, r.VoucherType.DisplayName(), r.Prefix, r.NextNumber)).ToList();
    }

    /// <summary>Only prefixes (and a higher starting number) can change; numbers never go backwards.</summary>
    public async Task SaveSequencesAsync(List<SequenceDto> items, CancellationToken ct)
    {
        foreach (var i in items)
        {
            var prefix = (i.Prefix ?? "").Trim();
            if (prefix.Length > 20 || prefix.Any(ch => !char.IsLetterOrDigit(ch) && ch is not ('-' or '/' or '_')))
                throw new DomainException($"{i.Name}: prefix may use letters, digits, - / _ only (max 20).");
            var row = await db.DocumentSequences.FirstOrDefaultAsync(s => s.FinancialYearId == user.FinancialYearId && s.VoucherType == i.VoucherType, ct);
            if (row is null)
            {
                db.DocumentSequences.Add(new DocumentSequence { FinancialYearId = user.FinancialYearId, VoucherType = i.VoucherType, Prefix = prefix, NextNumber = Math.Max(1, i.NextNumber) });
                continue;
            }
            if (i.NextNumber < row.NextNumber) throw new DomainException($"{i.Name}: the next number cannot go backwards (currently {row.NextNumber}).");
            row.Prefix = prefix;
            row.NextNumber = i.NextNumber;
        }
        audit.Log(Screens.Settings, "Edit", "Document numbering");
        await db.SaveChangesAsync(ct);
    }
}

public sealed record UserDto(long Id, string UserName, string DisplayName, string? Email, bool IsActive, long? RoleId, string? RoleName,
    string? Password, DateTimeOffset? LockoutEnd);

public class UserService(IAppDbContext db, ICurrentUser current, IPasswordHasher<User> hasher, AuditService audit, IClock clock, LicenseService license)
{
    public async Task<List<UserDto>> ListAsync(CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        return await db.UserCompanies.AsNoTracking().Where(m => m.CompanyId == current.CompanyId)
            .OrderBy(m => m.User!.UserName)
            .Select(m => new UserDto(m.UserId, m.User!.UserName, m.User.DisplayName, m.User.Email, m.User.IsActive, m.RoleId, m.Role!.Name, null, m.User.LockoutEnd))
            .ToListAsync(ct);
    }

    public async Task<long> SaveAsync(UserDto dto, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        if (string.IsNullOrWhiteSpace(dto.UserName)) throw new DomainException("User name is required.");
        if (dto.RoleId is null || !await db.Roles.AnyAsync(r => r.Id == dto.RoleId && r.IsActive, ct)) throw new DomainException("Select a role.");
        string norm = AuthService.Normalize(dto.UserName);
        await using var tx = await db.BeginAsync(ct);
        User u;
        if (dto.Id > 0)
        {
            u = await db.Users.Include(x => x.Companies).FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("User not found.");
            if (u.Companies.All(m => m.CompanyId != current.CompanyId)) throw new ForbiddenException("This user is not a member of the current company.");
            if (u.Id == current.UserId && !dto.IsActive) throw new DomainException("You cannot disable yourself.");
        }
        else
        {
            if (string.IsNullOrEmpty(dto.Password)) throw new DomainException("Set a password for the new user.");
            u = new User();
            db.Users.Add(u);
        }
        if (await db.Users.AnyAsync(x => x.NormalizedUserName == norm && x.Id != dto.Id, ct)) throw new DomainException("This user name is taken.", "duplicate");
        // The subscription limits active users per company: a new or re-activated member needs a free place.
        bool countedNow = dto.Id > 0 && u.IsActive && u.Companies.Any(m => m.CompanyId == current.CompanyId);
        if (dto.IsActive && !countedNow) await license.EnsureUserSlotAsync(current.CompanyId, ct);
        u.UserName = dto.UserName.Trim(); u.NormalizedUserName = norm; u.DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? u.UserName : dto.DisplayName.Trim();
        u.Email = dto.Email; u.IsActive = dto.IsActive;
        if (!string.IsNullOrEmpty(dto.Password))
        {
            AuthService.ValidatePassword(dto.Password);
            u.PasswordHash = hasher.HashPassword(u, dto.Password);
            u.SecurityStamp = Guid.NewGuid().ToString("N");
            u.LockoutEnd = null; u.FailedLoginCount = 0;
            if (dto.Id > 0) await db.RefreshTokens.Where(t => t.UserId == u.Id && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
        }
        var membership = u.Companies.FirstOrDefault(m => m.CompanyId == current.CompanyId);
        if (membership is null) u.Companies.Add(new UserCompany { CompanyId = current.CompanyId, RoleId = dto.RoleId.Value });
        else
        {
            if (u.Id == current.UserId && membership.RoleId != dto.RoleId) throw new DomainException("You cannot change your own role.");
            membership.RoleId = dto.RoleId.Value;
        }
        if (!dto.IsActive) await db.RefreshTokens.Where(t => t.UserId == u.Id && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
        audit.Log(Screens.Users, dto.Id == 0 ? "Add" : "Edit", u.UserName);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return u.Id;
    }

    public async Task RemoveFromCompanyAsync(long userId, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        if (userId == current.UserId) throw new DomainException("You cannot remove yourself.");
        var m = await db.UserCompanies.FirstOrDefaultAsync(x => x.UserId == userId && x.CompanyId == current.CompanyId, ct) ?? throw new NotFoundException("User not found.");
        db.UserCompanies.Remove(m);
        audit.Log(Screens.Users, "Delete", $"User #{userId} removed from company");
        await db.SaveChangesAsync(ct);
    }
}

public sealed record PermissionDto(string Screen, [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonNumberEnumConverter<Right>))] Right Rights);
public sealed record RoleDto(long Id, string Name, bool IsAdmin, bool IsActive, List<PermissionDto> Permissions, int UserCount);

public class RoleService(IAppDbContext db, AuditService audit, IPermissionCache cache)
{
    public async Task<List<RoleDto>> ListAsync(CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        var counts = await db.UserCompanies.GroupBy(m => m.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.IsAdmin, r.IsActive,
            Screens.All.Select(s => new PermissionDto(s, r.IsAdmin ? Right.All : r.Permissions.FirstOrDefault(p => p.Screen == s)?.Rights ?? Right.None)).ToList(),
            counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<long> SaveAsync(RoleDto dto, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new DomainException("Role name is required.");
        if (await db.Roles.AnyAsync(r => r.Name == dto.Name.Trim() && r.Id != dto.Id, ct)) throw new DomainException("A role with this name exists.");
        Role role;
        if (dto.Id > 0)
        {
            role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == dto.Id, ct) ?? throw new NotFoundException("Role not found.");
            if (role.IsAdmin && !dto.IsActive) throw new DomainException("The administrator role cannot be disabled.");
        }
        else { role = new Role(); db.Roles.Add(role); }
        role.Name = dto.Name.Trim();
        role.IsActive = dto.IsActive;
        if (!role.IsAdmin)
        {
            db.RolePermissions.RemoveRange(role.Permissions);
            role.Permissions.Clear();
            foreach (var p in dto.Permissions.Where(p => Screens.All.Contains(p.Screen) && p.Rights != Right.None))
            {
                // Any right implies read access to the screen.
                role.Permissions.Add(new RolePermission { Screen = p.Screen, Rights = p.Rights | Right.Read });
            }
        }
        audit.Log(Screens.Roles, dto.Id == 0 ? "Add" : "Edit", role.Name);
        await db.SaveChangesAsync(ct);
        cache.Invalidate(role.Id);
        return role.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Role not found.");
        if (role.IsAdmin) throw new DomainException("The administrator role cannot be deleted.");
        if (await db.UserCompanies.AnyAsync(m => m.RoleId == id, ct)) throw new DomainException("Users are assigned to this role.", "in_use");
        db.Roles.Remove(role);
        audit.Log(Screens.Roles, "Delete", role.Name);
        await db.SaveChangesAsync(ct);
        cache.Invalidate(id);
    }
}

/// <summary>Cached role â†’ screen rights map used by <see cref="ICurrentUser.HasRight"/>.</summary>
public interface IPermissionCache
{
    IReadOnlyDictionary<string, Right> Get(long roleId);
    void Invalidate(long roleId);
}

public sealed record UserLogRow(long Id, DateTimeOffset At, string UserName, string Screen, string Action, string? Remarks, string? Machine);

public class AuditQueryService(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public async Task<PagedResult<UserLogRow>> SearchAsync(DateOnly? from, DateOnly? to, string? userName, string? screen, string? action, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        var q = db.UserActionLogs.AsNoTracking().Where(l => l.CompanyId == user.CompanyId);
        if (from is not null) { var f = clock.DayStart(from.Value); q = q.Where(l => l.At >= f); }
        if (to is not null) { var t = clock.DayEnd(to.Value); q = q.Where(l => l.At < t); }
        if (!string.IsNullOrWhiteSpace(userName)) q = q.Where(l => l.UserName == userName);
        if (!string.IsNullOrWhiteSpace(screen)) q = q.Where(l => l.Screen == screen);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(l => l.Action == action);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(l => l.Remarks != null && l.Remarks.Contains(search));
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 500);
        var rows = await q.OrderByDescending(l => l.At).Skip((Math.Max(page, 1) - 1) * size).Take(size)
            .Select(l => new UserLogRow(l.Id, l.At, l.UserName, l.Screen, l.Action, l.Remarks, l.Machine)).ToListAsync(ct);
        return new PagedResult<UserLogRow>(rows, total, page, size);
    }
}
