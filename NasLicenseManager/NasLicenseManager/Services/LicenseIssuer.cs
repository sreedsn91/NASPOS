using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NasLicenseManager.Data;
using NasLicenseManager.Licensing;

namespace NasLicenseManager.Services;

public sealed class IssueException(string message) : Exception(message);

/// <summary>Where a customer stands, from their latest key.</summary>
public sealed record CustomerLicenseState(string State, string Css, IssuedLicense? Current, int? DaysLeft)
{
    public const int WarnDays = 30;

    public static CustomerLicenseState Of(IEnumerable<IssuedLicense> licenses, DateOnly today)
    {
        // The key that lasts longest is the one in force (lifetime beats everything).
        var current = licenses.OrderByDescending(l => l.ExpiresOn ?? DateOnly.MaxValue).ThenByDescending(l => l.IssuedAt).FirstOrDefault();
        if (current is null) return new("No key yet", "muted", null, null);
        if (current.ExpiresOn is not { } ends) return new("Lifetime", "ok", current, null);
        var left = ends.DayNumber - today.DayNumber;
        if (left < 0) return new("Expired", "danger", current, left);
        if (left < WarnDays) return new("Ending soon", "warn", current, left);
        return new("Active", "ok", current, left);
    }
}

/// <summary>Signs keys with the vendor's private key and records every key issued.</summary>
public class LicenseIssuer(LicensingDb db, SigningKeyStore keys)
{
    /// <summary>"1 month", "1 year 6 months", "Lifetime"…</summary>
    public static string PlanName(int? months) => LicenseKey.DurationText(months);

    public static string Dmy(DateOnly? d) => d?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "";

    public static string UsersText(int? users) => users is { } u ? $"{u} user{(u == 1 ? "" : "s")}" : "Unlimited users";

    /// <summary>Start date for a renewal: the day after the key in force ends (or today if it already ended).</summary>
    public static DateOnly RenewalStart(CustomerLicenseState state, DateOnly today) =>
        state.Current?.ExpiresOn is { } ends && ends >= today ? ends.AddDays(1) : today;

    public async Task<IssuedLicense> IssueAsync(int customerId, int? months, int? maxUsers, DateOnly startsOn, string? notes, string issuedBy, DateOnly today, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct) ?? throw new IssueException("Customer not found.");
        if (months is < 1 or > LicenseKey.MaxMonths) throw new IssueException($"The length must be from 1 month to {LicenseKey.MaxMonths / 12} years, or lifetime.");
        if (maxUsers is < 1 or > LicenseKey.MaxUserLimit) throw new IssueException($"Users must be between 1 and {LicenseKey.MaxUserLimit}, or unlimited.");
        if (startsOn < today.AddYears(-1) || startsOn > today.AddYears(2)) throw new IssueException("The start date must be within a year before or two years after today.");
        var gstin = string.IsNullOrWhiteSpace(customer.Gstin) ? null : LicenseKey.NormalizeGstin(customer.Gstin);

        string key;
        LicenseInfo info;
        using (var signer = keys.Load())
        {
            var product = ProductCodes.KeyProduct(customer.Product);
            key = LicenseKey.Create(signer, customer.CompanyName, gstin, months, startsOn, today, maxUsers, product);
            using var check = ECDsa.Create();
            check.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
            info = LicenseKey.Verify(key, customer.CompanyName, gstin, [check], product);
        }

        var issued = new IssuedLicense
        {
            CustomerId = customer.Id, KeyId = info.KeyId, Months = months, MaxUsers = maxUsers, StartsOn = startsOn, ExpiresOn = info.ExpiresOn,
            CompanyName = customer.CompanyName, Gstin = gstin, Product = customer.Product, Key = key, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            IssuedAt = DateTimeOffset.Now, IssuedBy = issuedBy,
        };
        db.IssuedLicenses.Add(issued);
        await db.SaveChangesAsync(ct);
        return issued;
    }

    /// <summary>Ready-to-send text for WhatsApp / e-mail.</summary>
    public static string CustomerMessage(IssuedLicense l) =>
        $"{ProductCodes.Name(l.Product)} license for {l.CompanyName}{(l.Gstin is { Length: > 0 } g ? $" (GSTIN {g})" : "")}\n" +
        $"Subscription: {PlanName(l.Months)} · {UsersText(l.MaxUsers)} · Valid {(l.ExpiresOn is null ? "for life" : $"from {Dmy(l.StartsOn)} until {Dmy(l.ExpiresOn)}")}\n\n" +
        $"License key:\n{l.Key}\n\n" +
        $"To apply it: open {ProductCodes.Name(l.Product)} › Settings › License › paste the key › Apply key.\n" +
        "(For a new company, paste it in the License key box while creating the company.)";
}
