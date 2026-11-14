using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Services;

namespace NasLicenseManager.Pages;

public sealed record CustomerRow(Customer Customer, CustomerLicenseState State);

public class IndexModel(LicensingDb db) : PageModel
{
    public int Customers { get; private set; }
    public int Active { get; private set; }
    public int EndingSoon { get; private set; }
    public int Expired { get; private set; }
    public int IssuedThisMonth { get; private set; }
    public List<CustomerRow> Attention { get; private set; } = [];
    public List<IssuedLicense> Recent { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var rows = (await db.Customers.AsNoTracking().Include(c => c.Licenses).ToListAsync())
            .Select(c => new CustomerRow(c, CustomerLicenseState.Of(c.Licenses, today))).ToList();
        Customers = rows.Count;
        Active = rows.Count(r => r.State.State is "Active" or "Lifetime");
        EndingSoon = rows.Count(r => r.State.State == "Ending soon");
        Expired = rows.Count(r => r.State.State == "Expired");
        var monthStart = new DateTimeOffset(new DateTime(today.Year, today.Month, 1));
        IssuedThisMonth = await db.IssuedLicenses.CountAsync(l => l.IssuedAt >= monthStart);
        Attention = rows.Where(r => r.State.State is "Ending soon" or "Expired" or "No key yet")
            .OrderBy(r => r.State.DaysLeft ?? int.MinValue).ToList();
        Recent = await db.IssuedLicenses.AsNoTracking().Include(l => l.Customer).OrderByDescending(l => l.IssuedAt).Take(8).ToListAsync();
    }
}
