using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;

namespace NasLicenseManager.Pages;

public class SetupModel(LicensingDb db, IPasswordHasher<StaffUser> hasher) : PageModel
{
    [BindProperty] public string UserName { get; set; } = "admin";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync() => await db.StaffUsers.AnyAsync() ? RedirectToPage("/Login") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await db.StaffUsers.AnyAsync()) return RedirectToPage("/Login");
        if (string.IsNullOrWhiteSpace(UserName)) Error = "Enter a user name.";
        else if (Auth.PasswordProblem(Password) is { } p) Error = p;
        else if (Password != Confirm) Error = "The passwords do not match.";
        if (Error is not null) return Page();

        var u = new StaffUser { UserName = UserName.Trim().ToLowerInvariant(), DisplayName = DisplayName.Trim(), IsAdmin = true, CreatedAt = DateTimeOffset.Now };
        u.PasswordHash = hasher.HashPassword(u, Password);
        db.StaffUsers.Add(u);
        await db.SaveChangesAsync();
        await Auth.SignInAsync(HttpContext, u);
        TempData["ok"] = "Administrator created. Next: check the signing key, then add your first customer.";
        return RedirectToPage("/SigningKey");
    }
}
