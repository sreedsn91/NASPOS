using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Kshethra.Api.Infrastructure;
using Kshethra.Api.Security;
using Kshethra.Application.Accounting;
using Kshethra.Application.Admin;
using Kshethra.Application.Auth;
using Kshethra.Application.Common;
using Kshethra.Application.Licensing;
using Kshethra.Application.Setup;
using Kshethra.Application.Tools;
using Kshethra.Domain;
using Kshethra.Domain.Validation;

namespace Kshethra.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AuthService auth, ICurrentUser user, CompanyService companies, IPermissionCache permissions,
    Application.Common.SettingsService settings, LicenseService license, IWebHostEnvironment env, IConfiguration config) : ControllerBase
{
    private const string RefreshCookie = "kshethra_rt";

    private void SetRefreshCookie(string? token, DateTimeOffset? expires)
    {
        if (token is null) return;
        // Security:SecureCookies=false is only for plain-HTTP LAN installs (browsers drop Secure cookies from http://<pc-name>).
        bool secure = Request.IsHttps || (!env.IsDevelopment() && config.GetValue("Security:SecureCookies", true));
        Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = "/api/auth",
            Expires = DateTimeOffset.UtcNow.AddDays(7),
        });
    }

    private object Public(LoginResult r) => new { r.AccessToken, r.ExpiresAt, r.Companies, r.RequiresCompany };

    [HttpGet("setup-status")]
    public async Task<object> SetupStatus(CancellationToken ct) => new { setupRequired = await auth.IsSetupRequiredAsync(ct), states = IndianStates.Names };

    [HttpPost("setup"), EnableRateLimiting("auth")]
    public async Task<object> Setup(SetupRequest r, CancellationToken ct)
    {
        var res = await auth.SetupAsync(r, user.IpAddress, ct);
        SetRefreshCookie(res.RefreshToken, res.ExpiresAt);
        return Public(res);
    }

    [HttpPost("login"), EnableRateLimiting("auth")]
    public async Task<object> Login(LoginRequest r, CancellationToken ct)
    {
        var res = await auth.LoginAsync(r, user.IpAddress, ct);
        SetRefreshCookie(res.RefreshToken, res.ExpiresAt);
        return Public(res);
    }

    [HttpPost("refresh"), EnableRateLimiting("refresh")]
    public async Task<IActionResult> Refresh([FromQuery] long? fy, CancellationToken ct)
    {
        var token = Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(token)) return Unauthorized();
        var res = await auth.RefreshAsync(token, fy, user.IpAddress, ct);
        SetRefreshCookie(res.RefreshToken, res.ExpiresAt);
        return Ok(Public(res));
    }

    [Authorize, HttpPost("switch")]
    public async Task<object> Switch(SwitchRequest r, CancellationToken ct)
    {
        var res = await auth.SwitchAsync(r, Request.Cookies[RefreshCookie], user.IpAddress, ct);
        SetRefreshCookie(res.RefreshToken, res.ExpiresAt);
        return Public(res);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(Request.Cookies[RefreshCookie], ct);
        Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth" });
        return NoContent();
    }

    [Authorize, HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest r, CancellationToken ct) { await auth.ChangePasswordAsync(r, ct); return NoContent(); }

    public sealed record ThemeRequest(string Theme);

    [Authorize, HttpPut("me/theme")]
    public async Task<IActionResult> SetTheme(ThemeRequest r, CancellationToken ct) { await auth.SetThemeAsync(r.Theme, ct); return NoContent(); }

    [Authorize, HttpPost("step-up"), EnableRateLimiting("auth")]
    public async Task<IssuedToken> StepUp([FromBody] StepUpRequest r, CancellationToken ct) => await auth.StepUpAsync(r.Password, ct);

    /// <summary>Session context: user, company, year, rights and settings (loaded at login).</summary>
    [Authorize, HttpGet("me")]
    public async Task<object> Me(CancellationToken ct)
    {
        var company = await companies.CurrentAsync(ct);
        var years = await companies.YearsAsync(ct);
        var roleId = long.TryParse(User.FindFirst(Claims.Role)?.Value, out var rid) ? rid : 0;
        var rights = user.IsAdmin
            ? Screens.All.ToDictionary(s => s, _ => (int)Right.All)
            : permissions.Get(roleId).ToDictionary(k => k.Key, v => (int)v.Value);
        return new
        {
            user = new { id = user.UserId, userName = user.UserName, displayName = User.FindFirst("name")?.Value, isAdmin = user.IsAdmin, theme = await auth.GetThemeAsync(ct) },
            company, financialYear = years.FirstOrDefault(y => y.Id == user.FinancialYearId), years, rights,
            settings = (await settings.GetAsync(ct)).ToDictionary(), counter = user.Machine,
            license = await license.CurrentAsync(ct),
        };
    }
}

/// <summary>Subscription license of the signed-in company: everyone may see it; entering a key needs Company › Edit.</summary>
[ApiController, Authorize]
[Route("api/license")]
public class LicenseController(LicenseService license) : ControllerBase
{
    [HttpGet]
    public Task<LicenseStatus> Get(CancellationToken ct) => license.CurrentAsync(ct);

    [HttpPut, Permission(Screens.Company, Right.Edit)]
    public Task<LicenseStatus> Apply(ApplyLicenseRequest r, CancellationToken ct) => license.ApplyAsync(r.Key, ct);
}

public sealed record StepUpRequest(string Password);

[ApiController, Authorize]
[Route("api/company")]
public class CompanyController(CompanyService companies, ICompanyBackupService backup, YearEndService yearEnd) : ControllerBase
{
    [HttpGet, Permission(Screens.Company, Right.Read)]
    public Task<CompanyDto> Get(CancellationToken ct) => companies.CurrentAsync(ct);

    [HttpPut, Permission(Screens.Company, Right.Edit)]
    public async Task<IActionResult> Update(CompanyDto dto, CancellationToken ct) { await companies.UpdateCurrentAsync(dto, ct); return NoContent(); }

    [HttpGet("logo")]
    public async Task<IActionResult> Logo(CancellationToken ct) => await companies.LogoAsync(ct) is { Length: > 0 } b ? File(b, "image/png") : NoContent();

    [HttpPut("logo"), Permission(Screens.Company, Right.Edit)]
    public async Task<IActionResult> SetLogo(IFormFile? file, CancellationToken ct)
    {
        byte[]? bytes = null;
        if (file is not null) { using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
        await companies.SetLogoAsync(bytes, ct);
        return NoContent();
    }

    [HttpGet("mine")]
    public Task<List<CompanyDto>> Mine(CancellationToken ct) => companies.MineAsync(ct);

    [HttpPost, Permission(Screens.Company, Right.Add)]
    public async Task<object> Create(CompanyDto dto, CancellationToken ct) => new { id = await companies.CreateAsync(dto, ct) };

    [HttpPut("{id:long}/visibility"), Permission(Screens.Company, Right.Edit)]
    public async Task<IActionResult> Visibility(long id, [FromQuery] bool hidden, [FromQuery] bool active, CancellationToken ct)
    { await companies.SetVisibilityAsync(id, hidden, active, ct); return NoContent(); }

    [HttpGet("settings"), Permission(Screens.Settings, Right.Read)]
    public Task<SettingsPayload> Settings(CancellationToken ct) => companies.SettingsAsync(ct);

    [HttpPut("settings"), Permission(Screens.Settings, Right.Edit)]
    public async Task<IActionResult> SaveSettings(Dictionary<string, string?> values, CancellationToken ct) { await companies.SaveSettingsAsync(values, ct); return NoContent(); }

    [HttpGet("invoice-options/{type}")]
    public Task<InvoiceOptions> InvoiceOptions(VoucherType type, CancellationToken ct) => companies.InvoiceOptionsAsync(type, ct);

    [HttpPut("invoice-options/{type}"), Permission(Screens.Settings, Right.Edit)]
    public async Task<IActionResult> SaveInvoiceOptions(VoucherType type, InvoiceOptions options, CancellationToken ct)
    { await companies.SaveInvoiceOptionsAsync(type, options, ct); return NoContent(); }

    [HttpGet("years")]
    public Task<List<FinancialYearDto>> Years(CancellationToken ct) => companies.YearsAsync(ct);

    [HttpPost("years/next"), Permission(Screens.YearEnd, Right.Add)]
    public async Task<object> CreateNextYear(CancellationToken ct) => new { id = await yearEnd.CreateNextYearAsync(ct) };

    [HttpGet("sequences"), Permission(Screens.Settings, Right.Read)]
    public Task<List<SequenceDto>> Sequences(CancellationToken ct) => companies.SequencesAsync(ct);

    [HttpPut("sequences"), Permission(Screens.Settings, Right.Edit)]
    public async Task<IActionResult> SaveSequences(List<SequenceDto> items, CancellationToken ct) { await companies.SaveSequencesAsync(items, ct); return NoContent(); }

    [HttpGet("backup"), Permission(Screens.Company, Right.Export)]
    public async Task<IActionResult> Backup(CancellationToken ct) =>
        File(await backup.ExportAsync(ct), "application/zip", $"company-backup-{DateTime.Now:yyyyMMddHHmm}.zip");

    [HttpPost("restore"), Permission(Screens.Company, Right.Add), RequireStepUp]
    public async Task<object> Restore(IFormFile file, [FromForm] string name, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return new { id = await backup.RestoreAsync(s, name, ct) };
    }
}

[ApiController, Authorize]
[Route("api/users")]
public class UsersController(UserService users, RoleService roles, AuditQueryService audit) : ControllerBase
{
    [HttpGet, Permission(Screens.Users, Right.Read)]
    public Task<List<UserDto>> List(CancellationToken ct) => users.ListAsync(ct);

    [HttpPost, Permission(Screens.Users, Right.Add)]
    public async Task<object> Create(UserDto dto, CancellationToken ct) => new { id = await users.SaveAsync(dto with { Id = 0 }, ct) };

    [HttpPut("{id:long}"), Permission(Screens.Users, Right.Edit)]
    public async Task<object> Update(long id, UserDto dto, CancellationToken ct) => new { id = await users.SaveAsync(dto with { Id = id }, ct) };

    [HttpDelete("{id:long}"), Permission(Screens.Users, Right.Delete), RequireStepUp]
    public async Task<IActionResult> Remove(long id, CancellationToken ct) { await users.RemoveFromCompanyAsync(id, ct); return NoContent(); }

    [HttpGet("/api/roles"), Permission(Screens.Roles, Right.Read)]
    public Task<List<RoleDto>> Roles(CancellationToken ct) => roles.ListAsync(ct);

    [HttpGet("/api/roles/screens"), Permission(Screens.Roles, Right.Read)]
    public string[] ScreenList() => Screens.All;

    [HttpPost("/api/roles"), Permission(Screens.Roles, Right.Add)]
    public async Task<object> CreateRole(RoleDto dto, CancellationToken ct) => new { id = await roles.SaveAsync(dto with { Id = 0 }, ct) };

    [HttpPut("/api/roles/{id:long}"), Permission(Screens.Roles, Right.Edit)]
    public async Task<object> UpdateRole(long id, RoleDto dto, CancellationToken ct) => new { id = await roles.SaveAsync(dto with { Id = id }, ct) };

    [HttpDelete("/api/roles/{id:long}"), Permission(Screens.Roles, Right.Delete)]
    public async Task<IActionResult> DeleteRole(long id, CancellationToken ct) { await roles.DeleteAsync(id, ct); return NoContent(); }

    [HttpGet("/api/audit"), Permission(Screens.UserLog, Right.Read)]
    public Task<PagedResult<UserLogRow>> Audit([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? user, [FromQuery] string? screen,
        [FromQuery] string? action, [FromQuery] string? search, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 100) =>
        audit.SearchAsync(from, to, user, screen, action, search, page, pageSize, ct);
}

[ApiController, Authorize]
[Route("api/dashboard")]
public class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet, Permission(Screens.Dashboard, Right.Read)]
    public Task<DashboardDto> Get(CancellationToken ct) => dashboard.GetAsync(ct);
}

[ApiController, Authorize]
[Route("api/year-end")]
public class YearEndController(YearEndService yearEnd) : ControllerBase
{
    [HttpGet("preview"), Permission(Screens.YearEnd, Right.Read)]
    public Task<YearEndPreview> Preview(CancellationToken ct) => yearEnd.PreviewAsync(ct);

    [HttpPost("close"), Permission(Screens.YearEnd, Right.Edit), RequireStepUp]
    public Task<YearEndResult> Close(CancellationToken ct) => yearEnd.CloseAsync(ct);
}

[ApiController, Authorize]
[Route("api/tools")]
public class ToolsController(ImportExportService io, TransferService transfer, ResetService reset) : ControllerBase
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpGet("products/export"), Permission(Screens.Product, Right.Export)]
    public async Task<IActionResult> ExportProducts([FromQuery] bool template, CancellationToken ct) =>
        File(await io.ExportProductsAsync(template, ct), Xlsx, template ? "products-template.xlsx" : "products.xlsx");

    [HttpPost("products/import"), Permission(Screens.Product, Right.Add)]
    public async Task<ImportResult> ImportProducts(IFormFile file, [FromQuery] bool openingStock, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return await io.ImportProductsAsync(s, openingStock, ct);
    }

    [HttpGet("ledgers/export"), Permission(Screens.Ledger, Right.Export)]
    public async Task<IActionResult> ExportLedgers([FromQuery] PartyType type, [FromQuery] bool template, CancellationToken ct) =>
        File(await io.ExportLedgersAsync(type, template, ct), Xlsx, $"{type.ToString().ToLowerInvariant()}s{(template ? "-template" : "")}.xlsx");

    [HttpPost("ledgers/import"), Permission(Screens.Ledger, Right.Add)]
    public async Task<ImportResult> ImportLedgers(IFormFile file, [FromQuery] PartyType type, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return await io.ImportLedgersAsync(s, type, ct);
    }

    [HttpGet("plu/export"), Permission(Screens.Product, Right.Export)]
    public async Task<IActionResult> ExportPlu(CancellationToken ct) => File(await io.ExportPluAsync(ct), "text/csv", "scale-plu.csv");

    [HttpPost("plu/import"), Permission(Screens.Product, Right.Edit)]
    public async Task<ImportResult> ImportPlu(IFormFile file, CancellationToken ct)
    {
        await using var s = file.OpenReadStream();
        return await io.ImportPluAsync(s, ct);
    }

    [HttpPost("transfer"), Permission(Screens.Tools, Right.Export)]
    public Task<TransferResult> Transfer(TransferRequest r, CancellationToken ct) => transfer.TransferAsync(r, ct);

    [HttpPost("reset/request"), Permission(Screens.Tools, Right.Delete)]
    public Task<ResetChallenge> ResetRequest(CancellationToken ct) => reset.RequestAsync(ct);

    [HttpPost("reset/confirm"), Permission(Screens.Tools, Right.Delete), RequireStepUp]
    public async Task<IActionResult> ResetConfirm(ResetConfirm r, CancellationToken ct) { await reset.ConfirmAsync(r, ct); return NoContent(); }
}
