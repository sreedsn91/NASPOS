using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;

namespace NasLicenseManager.Pages;

public class LoginModel(LicensingDb db, IPasswordHasher<StaffUser> hasher) : PageModel
{
    private const int MaxFailures = 5;

    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (UserName ?? "").Trim().ToLowerInvariant();
        var u = await db.StaffUsers.FirstOrDefaultAsync(x => x.UserName == name);
        if (u is { LockedUntil: { } until } && until > DateTimeOffset.Now)
        {
            Error = $"Too many wrong passwords. Try again after {until:HH:mm}.";
            return Page();
        }
        if (u is null || !u.IsActive || hasher.VerifyHashedPassword(u, u.PasswordHash, Password ?? "") == PasswordVerificationResult.Failed)
        {
            if (u is not null && ++u.FailedLogins >= MaxFailures) { u.LockedUntil = DateTimeOffset.Now.AddMinutes(15); u.FailedLogins = 0; }
            await db.SaveChangesAsync();
            Error = "Wrong user name or password.";
            return Page();
        }
        u.FailedLogins = 0;
        u.LockedUntil = null;
        await db.SaveChangesAsync();
        await Auth.SignInAsync(HttpContext, u);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
