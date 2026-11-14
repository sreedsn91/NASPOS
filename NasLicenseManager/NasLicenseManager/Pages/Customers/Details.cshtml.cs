using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Services;
using NasLicenseManager.Licensing;

namespace NasLicenseManager.Pages.Customers;

public class DetailsModel(LicensingDb db, LicenseIssuer issuer, SigningKeyStore keys) : PageModel
{
    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    /// <summary>Key just issued — shown at the top with copy buttons.</summary>
    [BindProperty(SupportsGet = true)] public int? Issued { get; set; }

    [BindProperty] public int Years { get; set; } = 1;
    /// <summary>Months on top of <see cref="Years"/> (0–11).</summary>
    [BindProperty] public int MonthsPart { get; set; }
    [BindProperty] public bool Lifetime { get; set; }
    [BindProperty] public int? MaxUsers { get; set; } = 5;
    [BindProperty] public bool UnlimitedUsers { get; set; }
    [BindProperty] public DateOnly StartsOn { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public Customer Customer { get; private set; } = null!;
    public CustomerLicenseState State { get; private set; } = null!;
    public IssuedLicense? JustIssued { get; private set; }
    public string? Error { get; set; }
    public bool SigningKeyReady => keys.Exists;
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private async Task<bool> LoadAsync()
    {
        var c = await db.Customers.AsNoTracking().Include(x => x.Licenses).FirstOrDefaultAsync(x => x.Id == Id);
        if (c is null) return false;
        c.Licenses = c.Licenses.OrderByDescending(l => l.IssuedAt).ToList();
        Customer = c;
        State = CustomerLicenseState.Of(c.Licenses, Today);
        if (Issued is { } id) JustIssued = c.Licenses.FirstOrDefault(l => l.Id == id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return NotFound();
        // A renewal starts the day after the current key ends, with the same length and users.
        StartsOn = LicenseIssuer.RenewalStart(State, Today);
        if (State.Current is { } cur)
        {
            Lifetime = cur.Months is null;
            (Years, MonthsPart) = cur.Months is { } m ? (m / 12, m % 12) : (1, 0);
            MaxUsers = cur.MaxUsers ?? 5;
            UnlimitedUsers = cur.MaxUsers is null;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostIssueAsync(CancellationToken ct)
    {
        if (!await LoadAsync()) return NotFound();
        int? users = UnlimitedUsers ? null : MaxUsers;
        int? months = Lifetime ? null : Years * 12 + MonthsPart;
        if (!Lifetime && (Years < 0 || MonthsPart is < 0 or > 11 || months < 1))
        { Error = "Enter the length: at least 1 month (months 0–11), or tick Lifetime."; return Page(); }
        if (!UnlimitedUsers && users is null) { Error = "Enter the number of users, or tick Unlimited."; return Page(); }
        try
        {
            var issued = await issuer.IssueAsync(Id, months, users, StartsOn, Notes, User.FindFirst("display")?.Value ?? User.Identity?.Name ?? "", Today, ct);
            TempData["ok"] = $"Key {issued.KeyId} issued for {issued.CompanyName}.";
            return Redirect($"/Customers/Details/{Id}?issued={issued.Id}");
        }
        catch (Exception ex) when (ex is IssueException or InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public string Message(IssuedLicense l) => LicenseIssuer.CustomerMessage(l);
}
