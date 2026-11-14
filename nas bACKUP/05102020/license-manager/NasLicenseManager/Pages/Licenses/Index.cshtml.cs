using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Services;

namespace NasLicenseManager.Pages.Licenses;

public class IndexModel(LicensingDb db) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    public List<IssuedLicense> Rows { get; private set; } = [];

    private IQueryable<IssuedLicense> Query()
    {
        var q = db.IssuedLicenses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var s = Q.Trim();
            q = q.Where(l => l.CompanyName.Contains(s) || l.KeyId.Contains(s) || (l.Gstin != null && l.Gstin.Contains(s)) || (l.Notes != null && l.Notes.Contains(s)));
        }
        if (From is { } f) { var start = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue)); q = q.Where(l => l.IssuedAt >= start); }
        if (To is { } t) { var end = new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue)); q = q.Where(l => l.IssuedAt < end); }
        return q.OrderByDescending(l => l.IssuedAt);
    }

    public async Task OnGetAsync() => Rows = await Query().Take(500).ToListAsync();

    /// <summary>All matching keys as CSV (opens in Excel).</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var rows = await Query().ToListAsync();
        var sb = new StringBuilder("Issued,Key ID,Company,GSTIN,Length,Users,Starts,Valid until,Notes,Issued by,Key\r\n");
        static string C(string? v)
        {
            v ??= "";
            if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }
        foreach (var l in rows)
            sb.Append(string.Join(',', new[]
            {
                l.IssuedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), l.KeyId, l.CompanyName, l.Gstin, LicenseIssuer.PlanName(l.Months),
                l.MaxUsers?.ToString(CultureInfo.InvariantCulture) ?? "Unlimited", LicenseIssuer.Dmy(l.StartsOn),
                l.ExpiresOn is null ? "Lifetime" : LicenseIssuer.Dmy(l.ExpiresOn), l.Notes, l.IssuedBy, l.Key,
            }.Select(C))).Append("\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"issued-keys-{DateTime.Today:yyyyMMdd}.csv");
    }
}
