using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Services;

namespace NasLicenseManager.Pages.Customers;

public class IndexModel(LicensingDb db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    /// <summary>"", "active", "ending", "expired" or "none".</summary>
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    public List<CustomerRow> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var query = db.Customers.AsNoTracking().Include(c => c.Licenses).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(c => c.CompanyName.Contains(q) || (c.Gstin != null && c.Gstin.Contains(q)) || (c.Phone != null && c.Phone.Contains(q))
                || (c.ContactPerson != null && c.ContactPerson.Contains(q)) || (c.City != null && c.City.Contains(q))
                || c.Licenses.Any(l => l.KeyId.Contains(q)));
        }
        Rows = (await query.OrderBy(c => c.CompanyName).ToListAsync())
            .Select(c => new CustomerRow(c, CustomerLicenseState.Of(c.Licenses, today)))
            .Where(r => Status switch
            {
                "active" => r.State.State is "Active" or "Lifetime",
                "ending" => r.State.State == "Ending soon",
                "expired" => r.State.State == "Expired",
                "none" => r.State.State == "No key yet",
                _ => true,
            }).ToList();
    }
}
