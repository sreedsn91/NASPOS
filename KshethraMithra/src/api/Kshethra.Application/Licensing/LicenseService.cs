using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Licensing;

namespace Kshethra.Application.Licensing;

/// <summary>License state of a company, for the banner and Settings › License.</summary>
/// <param name="State">"Active", "ExpiringSoon", "Grace" (no key yet, old company), "Expired", "Missing" or "Invalid"
/// (the stored key does not verify for this company).</param>
/// <param name="CanBill">False when new bills, purchases and vouchers are blocked.</param>
/// <param name="MaxUsers">Most active users allowed (null = unlimited).</param>
/// <param name="UsersInUse">Active users who belong to the company now.</param>
public sealed record LicenseStatus(string State, bool CanBill, string? Plan, DateOnly? ExpiresOn, int? DaysLeft, string? KeyId,
    DateOnly? GraceUntil, DateTimeOffset? AppliedAt, string Message, int? MaxUsers = null, int UsersInUse = 0);

public sealed record ApplyLicenseRequest(string? Key);

/// <summary>
/// Subscription keys per company. A key is checked offline (signature + company name/GSTIN) when a company is created
/// or a new key is entered, and the stored key is checked again before its expiry and user limit are used, so editing
/// the License* columns in the database changes nothing. Viewing, reports, printing and backups always keep working.
/// </summary>
public class LicenseService(IAppDbContext db, ICurrentUser user, IClock clock, AuditService audit)
{
    /// <summary>Warn this many days before a license ends.</summary>
    public const int WarnDays = 30;
    /// <summary>Companies that existed before licensing get this long to enter a key.</summary>
    public const int GraceDaysForExistingCompanies = 30;

    /// <summary>Verifies a key for a company name/GSTIN; turns key problems into user-facing errors.</summary>
    public static LicenseInfo Check(string? key, string companyName, string? gstin, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("Enter the license key. Contact Kshethra Mithra support to get one.", "license_required");
        LicenseInfo info;
        try { info = LicenseKey.Verify(key, companyName, gstin, LicenseTrust.Trusted); }
        catch (LicenseKeyException ex) { throw new DomainException(ex.Message, ex.Code); }
        if (info.IsExpiredOn(today))
            throw new DomainException($"This license key expired on {Dmy(info.ExpiresOn)}. Contact Kshethra Mithra support for a new key.", "license_expired");
        return info;
    }

    /// <summary>Stored keys already checked in this run, so each key's signature is verified once per company name/GSTIN.</summary>
    private static readonly ConcurrentDictionary<(string Key, string Name, string? Gstin), LicenseInfo?> Verified = new();

    /// <summary>
    /// What a stored key says, after checking its signature and that it belongs to this name/GSTIN. Null = no key, or
    /// the key does not verify (altered, or the company's name/GSTIN no longer matches it).
    /// </summary>
    public static LicenseInfo? StoredKey(string? key, string companyName, string? gstin) =>
        key is null ? null : Verified.GetOrAdd((key, companyName, gstin), k =>
        {
            try { return LicenseKey.Verify(k.Key, k.Name, k.Gstin, LicenseTrust.Trusted); }
            catch (LicenseKeyException) { return null; }
        });

    public static LicenseInfo? StoredKey(Company c) => StoredKey(c.LicenseKey, c.Name, c.Gstin);

    /// <summary>Stores a verified key on the company. The License* columns are a copy for display; the key decides.</summary>
    public static void Apply(Company c, LicenseInfo info, string key, DateTimeOffset now)
    {
        c.LicenseKey = key.Trim().ToUpperInvariant();
        c.LicenseKeyId = info.KeyId;
        c.LicenseMonths = info.Months;
        c.LicenseExpiresOn = info.ExpiresOn;
        c.LicenseMaxUsers = info.MaxUsers;
        c.LicenseAppliedAt = now;
    }

    public static string UserLimitMessage(int max) =>
        $"Your subscription allows {max} user{(max == 1 ? "" : "s")} and all are in use. Deactivate a user you no longer need, " +
        "or contact Kshethra Mithra support for a new subscription with more users.";

    /// <summary>Active users who are members of the company (they count against the license).</summary>
    public Task<int> ActiveUsersAsync(long companyId, CancellationToken ct) =>
        db.UserCompanies.CountAsync(m => m.CompanyId == companyId && m.User!.IsActive, ct);

    /// <summary>Call before adding or re-activating a user in a company: blocks it when the license's user limit is reached.</summary>
    public async Task EnsureUserSlotAsync(long companyId, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().Where(x => x.Id == companyId).Select(x => new { x.LicenseKey, x.Name, x.Gstin }).FirstOrDefaultAsync(ct);
        if (c?.LicenseKey is null) return;
        if (StoredKey(c.LicenseKey, c.Name, c.Gstin) is not { } info) throw new DomainException(InvalidMessage, "license_invalid");
        if (info.MaxUsers is not { } max) return;
        if (await ActiveUsersAsync(companyId, ct) >= max) throw new DomainException(UserLimitMessage(max), "user_limit");
    }

    private const string InvalidMessage =
        "The license key does not match this temple's name or GSTIN, or has been altered. New bills can't be saved until " +
        "a valid key is entered in Settings › License. Contact Kshethra Mithra support.";

    public static LicenseStatus StatusOf(Company c, DateOnly today)
    {
        if (c.LicenseKey is null)
        {
            if (c.LicenseGraceUntil is { } grace && today <= grace)
            {
                var left = grace.DayNumber - today.DayNumber;
                return new("Grace", true, null, null, left, null, grace, null,
                    $"No license key yet. Billing works until {Dmy(grace)} ({Days(left)} left). Enter a key in Settings › License.");
            }
            return new("Missing", false, null, null, null, null, c.LicenseGraceUntil, null,
                "No license key. New bills can't be saved until a key is entered in Settings › License. Contact Kshethra Mithra support to get one.");
        }
        if (StoredKey(c) is not { } info)
            return new("Invalid", false, null, null, null, c.LicenseKeyId, null, c.LicenseAppliedAt, InvalidMessage);
        var plan = LicenseKey.DurationText(info.Months);
        if (info.ExpiresOn is not { } ends)
            return new("Active", true, plan, null, null, info.KeyId, null, c.LicenseAppliedAt, "Licensed for life.");
        var daysLeft = ends.DayNumber - today.DayNumber;
        if (daysLeft < 0)
            return new("Expired", false, plan, ends, daysLeft, info.KeyId, null, c.LicenseAppliedAt,
                $"License expired on {Dmy(ends)}. New bills can't be saved until a new key is entered in Settings › License. Contact Kshethra Mithra support to renew.");
        if (daysLeft < WarnDays)
            return new("ExpiringSoon", true, plan, ends, daysLeft, info.KeyId, null, c.LicenseAppliedAt,
                $"License ends on {Dmy(ends)} ({Days(daysLeft + 1)} left, including today). Contact Kshethra Mithra support for a renewal key.");
        return new("Active", true, plan, ends, daysLeft, info.KeyId, null, c.LicenseAppliedAt, $"Licensed ({plan}) until {Dmy(ends)}.");
    }

    /// <summary>Status with the company's user count and limit filled in.</summary>
    private async Task<LicenseStatus> WithUsersAsync(Company c, DateOnly today, CancellationToken ct) =>
        StatusOf(c, today) with { MaxUsers = StoredKey(c)?.MaxUsers, UsersInUse = await ActiveUsersAsync(c.Id, ct) };

    /// <summary>
    /// Today's business date, never earlier than the latest date already seen: turning the PC clock back does not
    /// bring an expired license back. Records today when the calendar moves on.
    /// </summary>
    private async Task<DateOnly> TodayAsync(Company c, CancellationToken ct)
    {
        var today = clock.Today();
        if (c.LicenseLastSeenOn is { } seen && seen > today) return seen;
        if (c.LicenseLastSeenOn is null || c.LicenseLastSeenOn < today)
        {
            // Direct update: no row-version clash with other counters saving bills at the same moment.
            await db.Companies.Where(x => x.Id == c.Id && (x.LicenseLastSeenOn == null || x.LicenseLastSeenOn < today))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LicenseLastSeenOn, today), ct);
            c.LicenseLastSeenOn = today;
        }
        return today;
    }

    private Task<Company> CompanyAsync(CancellationToken ct) =>
        db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);

    public async Task<LicenseStatus> CurrentAsync(CancellationToken ct)
    {
        var c = await CompanyAsync(ct);
        return await WithUsersAsync(c, await TodayAsync(c, ct), ct);
    }

    /// <summary>Stops saving bills, purchases and vouchers when the company's license has ended.</summary>
    public async Task EnsureCanTransactAsync(CancellationToken ct)
    {
        var c = await CompanyAsync(ct);
        var status = StatusOf(c, await TodayAsync(c, ct));
        if (!status.CanBill) throw new DomainException(status.Message, "license_expired");
    }

    /// <summary>Settings › License: enter a new key (renewal or first key) for the current company.</summary>
    public async Task<LicenseStatus> ApplyAsync(string? key, CancellationToken ct)
    {
        var c = await db.Companies.FirstAsync(x => x.Id == user.CompanyId, ct);
        var today = await TodayAsync(c, ct);
        await ReplaceKeyAsync(c, key, c.Name, c.Gstin, today, ct);
        await db.SaveChangesAsync(ct);
        return await WithUsersAsync(c, today, ct);
    }

    /// <summary>
    /// Checks a new key for the company under <paramref name="name"/>/<paramref name="gstin"/> (its current details, or
    /// the new ones when they are being changed) and stores it on <paramref name="c"/>. The caller saves.
    /// </summary>
    public async Task ReplaceKeyAsync(Company c, string? key, string name, string? gstin, DateOnly today, CancellationToken ct)
    {
        var info = Check(key, name, gstin, today);
        var current = StoredKey(c); // an invalid stored key counts as no license, so any valid key replaces it
        if (current is not null && current.KeyId == info.KeyId)
            throw new DomainException("This key is already active for this temple.", "license_same");
        if (current is not null && current.ExpiresOn is null && info.ExpiresOn is not null)
            throw new DomainException("This temple already has a lifetime license; the new key would shorten it.", "license_shorter");
        if (current?.ExpiresOn is { } ends && info.ExpiresOn is { } next && next < ends)
            throw new DomainException($"This key ends on {Dmy(next)}, before the current license ({Dmy(ends)}).", "license_shorter");
        if (info.MaxUsers is { } allowed && await ActiveUsersAsync(c.Id, ct) is var active && active > allowed)
            throw new DomainException($"This key allows {allowed} users, but {active} users are active in this temple. Deactivate {active - allowed} " +
                $"in Settings › Users first, or ask Kshethra Mithra support for a key with more users.", "user_limit");
        Apply(c, info, key!, clock.Now);
        audit.Log(Screens.Company, "Edit", $"License key {info.KeyId} applied ({LicenseKey.DurationText(info.Months)}{(info.ExpiresOn is { } e ? $", until {Dmy(e)}" : "")}" +
            $", {(info.MaxUsers is { } m ? $"{m} users" : "unlimited users")})");
    }


    private static string Dmy(DateOnly? d) => d?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "";
    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";
}
