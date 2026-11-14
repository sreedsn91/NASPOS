using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;

namespace NasLicenseManager.Pages;

/// <summary>Administrators manage who can sign in and issue keys.</summary>
public class StaffModel(LicensingDb db, IPasswordHasher<StaffUser> hasher) : PageModel
{
    public List<StaffUser> Users { get; private set; } = [];
    [BindProperty] public string UserName { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool IsAdmin { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync() => Users = await db.StaffUsers.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();

    public async Task<IActionResult> OnPostAddAsync()
    {
        var name = (UserName ?? "").Trim().ToLowerInvariant();
        if (name.Length is 0 or > 60 || !name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) Error = "User name: letters, digits, . _ - only.";
        else if (Auth.PasswordProblem(Password) is { } p) Error = p;
        else if (await db.StaffUsers.AnyAsync(u => u.UserName == name)) Error = "This user name is taken.";
        if (Error is not null) { await OnGetAsync(); return Page(); }
        var u = new StaffUser { UserName = name, DisplayName = (DisplayName ?? "").Trim(), IsAdmin = IsAdmin, CreatedAt = DateTimeOffset.Now };
        u.PasswordHash = hasher.HashPassword(u, Password);
        db.StaffUsers.Add(u);
        await db.SaveChangesAsync();
        TempData["ok"] = $"{name} can now sign in.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var u = await db.StaffUsers.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return RedirectToPage();
        if (u.Id == Auth.UserId(User)) { TempData["error"] = "You cannot disable yourself."; return RedirectToPage(); }
        if (u.IsActive && u.IsAdmin && await db.StaffUsers.CountAsync(x => x.IsAdmin && x.IsActive) == 1) { TempData["error"] = "Keep at least one active administrator."; return RedirectToPage(); }
        u.IsActive = !u.IsActive;
        await db.SaveChangesAsync();
        TempData["ok"] = $"{u.UserName} is now {(u.IsActive ? "active" : "disabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetAsync(int id, string newPassword)
    {
        var u = await db.StaffUsers.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return RedirectToPage();
        if (Auth.PasswordProblem(newPassword) is { } p) { TempData["error"] = p; return RedirectToPage(); }
        u.PasswordHash = hasher.HashPassword(u, newPassword);
        u.FailedLogins = 0; u.LockedUntil = null;
        await db.SaveChangesAsync();
        TempData["ok"] = $"Password for {u.UserName} changed.";
        return RedirectToPage();
    }
}
