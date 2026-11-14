using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;

namespace NasLicenseManager.Pages.Account;

public class PasswordModel(LicensingDb db, IPasswordHasher<StaffUser> hasher) : PageModel
{
    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string current, string next, string confirm)
    {
        var u = await db.StaffUsers.FirstAsync(x => x.Id == Auth.UserId(User));
        if (hasher.VerifyHashedPassword(u, u.PasswordHash, current ?? "") == PasswordVerificationResult.Failed) Error = "Current password is wrong.";
        else if (Auth.PasswordProblem(next) is { } p) Error = p;
        else if (next != confirm) Error = "The new passwords do not match.";
        if (Error is not null) return Page();
        u.PasswordHash = hasher.HashPassword(u, next);
        await db.SaveChangesAsync();
        TempData["ok"] = "Password changed.";
        return Redirect("/");
    }
}
