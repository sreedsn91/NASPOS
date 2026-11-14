using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasPos.Domain.Licensing;

namespace NasLicenseManager.Pages.Customers;

public class EditModel(LicensingDb db) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public string CompanyName { get; set; } = "";
    [BindProperty] public string? Gstin { get; set; }
    [BindProperty] public string? ContactPerson { get; set; }
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string? Notes { get; set; }
    public string? Error { get; set; }
    public bool HasKeys { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is null) return Page();
        var c = await db.Customers.AsNoTracking().Include(x => x.Licenses).FirstOrDefaultAsync(x => x.Id == Id);
        if (c is null) return NotFound();
        (CompanyName, Gstin, ContactPerson, Phone, Email, City, Notes) = (c.CompanyName, c.Gstin, c.ContactPerson, c.Phone, c.Email, c.City, c.Notes);
        HasKeys = c.Licenses.Count > 0;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (CompanyName ?? "").Trim();
        var gstin = LicenseKey.NormalizeGstin(Gstin);
        if (LicenseKey.NormalizeName(name).Length == 0) Error = "Enter the company name.";
        else if (name.Length > 200) Error = "Company name is too long.";
        else if (gstin.Length is > 0 and not 15) Error = "A GSTIN has 15 characters. Leave it empty if the company has none.";
        else if (Email is { Length: > 0 } e && !System.Net.Mail.MailAddress.TryCreate(e.Trim(), out _)) Error = "E-mail is not valid.";
        if (Error is null)
        {
            var normalized = LicenseKey.NormalizeName(name);
            var others = await db.Customers.Where(x => x.Id != (Id ?? 0)).Select(x => new { x.CompanyName, x.Gstin }).ToListAsync();
            if (others.Any(o => LicenseKey.NormalizeName(o.CompanyName) == normalized && (o.Gstin ?? "") == gstin))
                Error = "A customer with this company name (and GSTIN) already exists.";
        }
        if (Error is not null) return Page();

        Customer c;
        if (Id is null)
        {
            c = new Customer { CreatedAt = DateTimeOffset.Now, CreatedBy = User.Identity?.Name ?? "" };
            db.Customers.Add(c);
        }
        else c = await db.Customers.FirstOrDefaultAsync(x => x.Id == Id) ?? throw new InvalidOperationException("Customer not found.");
        c.CompanyName = name;
        c.Gstin = gstin.Length == 0 ? null : gstin;
        c.ContactPerson = Trim(ContactPerson, 100);
        c.Phone = Trim(Phone, 40);
        c.Email = Trim(Email, 200);
        c.City = Trim(City, 100);
        c.Notes = Trim(Notes, 2000);
        await db.SaveChangesAsync();
        TempData["ok"] = Id is null ? "Customer added. Issue their key below." : "Customer saved.";
        return Redirect($"/Customers/Details/{c.Id}" + (Id is null ? "#issue" : ""));
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
