using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Application.Licensing;
using Kshethra.Application.Setup;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Validation;

namespace Kshethra.Application.Auth;

public sealed record TokenRequest(User User, long CompanyId, long FinancialYearId, long RoleId, bool IsAdmin);
public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    IssuedToken CreateAccessToken(TokenRequest request);
    IssuedToken CreateStepUpToken(long userId);
    bool ValidateStepUpToken(string token, long userId);
    TimeSpan RefreshLifetime { get; }
}

public sealed record LoginRequest(string UserName, string Password, long? CompanyId);
public sealed record CompanyChoice(long Id, string Name, string RoleName);
public sealed record LoginResult(string? AccessToken, DateTimeOffset? ExpiresAt, string? RefreshToken, IReadOnlyList<CompanyChoice> Companies,
    bool RequiresCompany);
public sealed record SetupRequest(string CompanyName, string StateCode, string? Gstin, string? Address, string? City, string? Phone,
    string AdminUserName, string AdminPassword, string? AdminDisplayName, DateOnly? FinancialYearStart, string? LicenseKey = null);
public sealed record SwitchRequest(long CompanyId, long? FinancialYearId);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public class AuthService(IAppDbContext db, ITokenService tokens, IPasswordHasher<User> hasher, IClock clock,
    CompanySetupService setup, AuditService audit, ICurrentUser current)
{
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(15);

    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();

    public static void ValidatePassword(string password)
    {
        if (password.Length < 8 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new DomainException("Password must be at least 8 characters and contain letters and digits.", "weak_password");
    }

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct) => !await db.Users.AnyAsync(ct);

    /// <summary>First-run setup: creates the first company and the administrator chosen by the operator (no default password ships).</summary>
    public async Task<LoginResult> SetupAsync(SetupRequest r, string? ip, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        if (await db.Users.AnyAsync(ct)) throw new ConflictException("Setup has already been completed.");
        ValidatePassword(r.AdminPassword);
        if (string.IsNullOrWhiteSpace(r.CompanyName)) throw new DomainException("Temple name is required.");
        if (!string.IsNullOrWhiteSpace(r.Gstin))
        {
            Gstin.EnsureValid(r.Gstin);
            if (Gstin.StateCode(r.Gstin) != r.StateCode?.Trim())
                throw new DomainException($"The GSTIN belongs to state {Gstin.StateCode(r.Gstin)}, but state {r.StateCode} is selected.", "state_mismatch");
        }

        var company = new Company
        {
            Name = r.CompanyName.Trim(), LegalName = r.CompanyName.Trim(), StateCode = r.StateCode.Trim(),
            Gstin = string.IsNullOrWhiteSpace(r.Gstin) ? null : Gstin.Normalize(r.Gstin), Address1 = r.Address, City = r.City, Phone = r.Phone,
        };
        // A subscription key issued for this company name (and GSTIN, if the key carries one) is required.
        var license = LicenseService.Check(r.LicenseKey, company.Name, company.Gstin, clock.Today());
        LicenseService.Apply(company, license, r.LicenseKey!, clock.Now);
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        var fyStart = r.FinancialYearStart ?? CompanySetupService.FinancialYearFor(DateOnly.FromDateTime(clock.Now.Date)).Start;
        await setup.SeedAsync(company, fyStart, ct);

        var adminRole = await db.Roles.FirstOrDefaultAsync(x => x.IsAdmin, ct);
        if (adminRole is null)
        {
            adminRole = new Role { Name = "Administrator", IsAdmin = true };
            db.Roles.Add(adminRole);
        }
        var user = new User
        {
            UserName = r.AdminUserName.Trim(), NormalizedUserName = Normalize(r.AdminUserName),
            DisplayName = string.IsNullOrWhiteSpace(r.AdminDisplayName) ? r.AdminUserName.Trim() : r.AdminDisplayName.Trim(),
        };
        user.PasswordHash = hasher.HashPassword(user, r.AdminPassword);
        user.Companies.Add(new UserCompany { Company = company, Role = adminRole });
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return await LoginAsync(new LoginRequest(r.AdminUserName, r.AdminPassword, company.Id), ip, ct);
    }

    public async Task<LoginResult> LoginAsync(LoginRequest r, string? ip, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var user = await db.Users.Include(u => u.Companies).ThenInclude(c => c.Role)
            .Include(u => u.Companies).ThenInclude(c => c.Company)
            .FirstOrDefaultAsync(u => u.NormalizedUserName == Normalize(r.UserName ?? ""), ct);

        if (user is null)
        {
            // Hash anyway so response time doesn't reveal whether the user exists.
            hasher.HashPassword(new User(), r.Password ?? "");
            throw new ForbiddenException("Invalid user name or password.");
        }
        if (!user.IsActive) throw new ForbiddenException("This user is disabled.");
        if (user.LockoutEnd > clock.Now) throw new ForbiddenException("Too many failed attempts. Try again later.");

        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password ?? "");
        if (verify == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins) { user.LockoutEnd = clock.Now.Add(LockoutTime); user.FailedLoginCount = 0; }
            db.UserActionLogs.Add(new UserActionLog { UserId = user.Id, UserName = user.UserName, Screen = "Login", Action = "LoginFailed", Machine = ip, At = clock.Now });
            await db.SaveChangesAsync(ct);
            throw new ForbiddenException("Invalid user name or password.");
        }
        if (verify == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, r.Password!);
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;

        var memberships = user.Companies.Where(c => c.Company!.IsActive && c.Role!.IsActive).ToList();
        var choices = memberships.Select(m => new CompanyChoice(m.CompanyId, m.Company!.Name, m.Role!.Name)).OrderBy(c => c.Name).ToList();
        if (choices.Count == 0) throw new ForbiddenException("This user has no access to any temple.");

        long? companyId = r.CompanyId ?? (choices.Count == 1 ? choices[0].Id : null);
        if (companyId is null)
        {
            await db.SaveChangesAsync(ct);
            return new LoginResult(null, null, null, choices, true);
        }
        var membership = memberships.FirstOrDefault(m => m.CompanyId == companyId)
                         ?? throw new ForbiddenException("No access to the selected temple.");

        var fy = await DefaultYearAsync(membership.CompanyId, ct);
        var result = await IssueAsync(user, membership, fy, ip, ct);
        db.UserActionLogs.Add(new UserActionLog { CompanyId = membership.CompanyId, UserId = user.Id, UserName = user.UserName, Screen = "Login", Action = "Login", Machine = ip, At = clock.Now });
        await db.SaveChangesAsync(ct);
        return result with { Companies = choices };
    }

    private async Task<long> DefaultYearAsync(long companyId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.Now.Date);
        var years = await db.FinancialYears.Where(f => f.CompanyId == companyId).OrderByDescending(f => f.StartDate).ToListAsync(ct);
        return (years.FirstOrDefault(f => f.Contains(today) && !f.IsClosed) ?? years.FirstOrDefault(f => !f.IsClosed) ?? years.First()).Id;
    }

    private async Task<LoginResult> IssueAsync(User user, UserCompany m, long fyId, string? ip, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(new TokenRequest(user, m.CompanyId, fyId, m.RoleId, m.Role!.IsAdmin));
        string refresh = NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id, CompanyId = m.CompanyId, TokenHash = Hash(refresh), CreatedAt = clock.Now,
            ExpiresAt = clock.Now.Add(tokens.RefreshLifetime), CreatedByIp = ip,
        });
        await Task.CompletedTask;
        return new LoginResult(access.Token, access.ExpiresAt, refresh, [], false);
    }

    /// <summary>Rotates the refresh token. Presenting an already-rotated token revokes the whole family (theft detection).</summary>
    public async Task<LoginResult> RefreshAsync(string refreshToken, long? financialYearId, string? ip, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                     ?? throw new ForbiddenException("Session expired. Please sign in again.");
        if (stored.RevokedAt is not null)
        {
            await db.RefreshTokens.Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
            throw new ForbiddenException("Session expired. Please sign in again.");
        }
        if (stored.ExpiresAt <= clock.Now) throw new ForbiddenException("Session expired. Please sign in again.");

        var user = await db.Users.Include(u => u.Companies).ThenInclude(c => c.Role).FirstAsync(u => u.Id == stored.UserId, ct);
        var m = user.Companies.FirstOrDefault(c => c.CompanyId == stored.CompanyId);
        if (!user.IsActive || m is null || !m.Role!.IsActive) throw new ForbiddenException("Access revoked.");

        long fy = financialYearId is > 0 && await db.FinancialYears.AnyAsync(f => f.Id == financialYearId && f.CompanyId == m.CompanyId, ct)
            ? financialYearId.Value
            : await DefaultYearAsync(m.CompanyId, ct);
        var result = await IssueAsync(user, m, fy, ip, ct);
        stored.RevokedAt = clock.Now;
        stored.ReplacedByHash = Hash(result.RefreshToken!);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<LoginResult> SwitchAsync(SwitchRequest r, string? currentRefresh, string? ip, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var user = await db.Users.Include(u => u.Companies).ThenInclude(c => c.Role).FirstAsync(u => u.Id == current.UserId, ct);
        var m = user.Companies.FirstOrDefault(c => c.CompanyId == r.CompanyId && c.Role!.IsActive)
                ?? throw new ForbiddenException("No access to the selected temple.");
        long fy = r.FinancialYearId is > 0 && await db.FinancialYears.AnyAsync(f => f.Id == r.FinancialYearId && f.CompanyId == r.CompanyId, ct)
            ? r.FinancialYearId.Value
            : await DefaultYearAsync(r.CompanyId, ct);
        if (currentRefresh is not null)
            await db.RefreshTokens.Where(t => t.TokenHash == Hash(currentRefresh))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
        var result = await IssueAsync(user, m, fy, ip, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        using var _ = db.IgnoreTenant();
        await db.RefreshTokens.Where(t => t.TokenHash == Hash(refreshToken) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
    }

    public static readonly string[] Themes = ["light", "dark", "system", "ocean", "sunset"];

    public async Task<string> GetThemeAsync(CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        return await db.Users.Where(u => u.Id == current.UserId).Select(u => u.Theme).FirstOrDefaultAsync(ct) ?? "light";
    }

    /// <summary>Personal preference: no screen right needed, only the signed-in user's own row changes.</summary>
    public async Task SetThemeAsync(string theme, CancellationToken ct)
    {
        var t = (theme ?? "").Trim().ToLowerInvariant();
        if (!Themes.Contains(t)) throw new DomainException("Unknown theme. Choose light, dark, ocean, sunset or system.", "invalid_theme");
        using var _ = db.IgnoreTenant();
        var user = await db.Users.FirstAsync(u => u.Id == current.UserId, ct);
        user.Theme = t;
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var user = await db.Users.FirstAsync(u => u.Id == current.UserId, ct);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, r.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new DomainException("Current password is incorrect.", "wrong_password");
        ValidatePassword(r.NewPassword);
        user.PasswordHash = hasher.HashPassword(user, r.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, clock.Now), ct);
        audit.Log("Users", "ChangePassword", user.UserName);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Re-authentication for destructive actions (cancel/delete bill, year-end, reset).</summary>
    public async Task<IssuedToken> StepUpAsync(string password, CancellationToken ct)
    {
        using var _ = db.IgnoreTenant();
        var user = await db.Users.FirstAsync(u => u.Id == current.UserId, ct);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password) == PasswordVerificationResult.Failed)
            throw new ForbiddenException("Password is incorrect.");
        return tokens.CreateStepUpToken(user.Id);
    }

    private static string NewRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
