using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Accounting;
using Kshethra.Application.Common;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Temple;

public sealed record DonationRequest(string? DonorName, string? HouseName, string? Phone, string? Address, int? Star, long? DevoteeId,
    long PurposeLedgerId, decimal Amount, string Mode, long? BankLedgerId, string? Reference, string? Pan, string? Notes);

public sealed record DonationDto(long Id, string ReceiptNo, DateTimeOffset Date, string DonorName, string? HouseName, string? Phone, string? Address,
    int? Star, string? StarName, long PurposeLedgerId, string Purpose, decimal Amount, string ReceivedInto, string? Reference, string? Pan,
    string? Notes, bool Cancelled, long VoucherId, string? UserName);

public sealed record DonationPurpose(long Id, string Name, string? RegionalName);

/// <summary>
/// Donations (kanikka / sambhavana). Each one is posted as a receipt voucher: cash or bank to the donation head (an
/// income ledger in the "Donations" group), so it appears in the day book, cash book and income &amp; expenditure.
/// </summary>
public class DonationService(IAppDbContext db, ICurrentUser user, IClock clock, VoucherService vouchers, SystemLedgerResolver systemLedgers, AuditService audit)
{
    public const string DonationsGroup = "Donations";

    public async Task<List<DonationPurpose>> PurposesAsync(CancellationToken ct) =>
        await db.Ledgers.AsNoTracking().Where(l => l.IsActive && l.AccountGroup!.Name == DonationsGroup).OrderBy(l => l.Name)
            .Select(l => new DonationPurpose(l.Id, l.Name, l.RegionalName)).ToListAsync(ct);

    public async Task<DonationDto> CreateAsync(DonationRequest r, CancellationToken ct)
    {
        var name = (r.DonorName ?? "").Trim();
        if (name.Length == 0) throw new DomainException("Enter the donor's name.");
        if (name.Length > 150) throw new DomainException("Donor name is too long.");
        if (r.Amount <= 0) throw new DomainException("Enter the amount.");
        if (r.Amount > 100_000_000) throw new DomainException("Amount is too large.");
        if (r.Star is not null and not (>= 0 and < 27)) throw new DomainException("Choose a valid star.");
        var pan = string.IsNullOrWhiteSpace(r.Pan) ? null : r.Pan.Trim().ToUpperInvariant();
        if (pan is not null && !System.Text.RegularExpressions.Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$")) throw new DomainException("PAN is not valid (e.g. ABCDE1234F).");
        var purpose = await db.Ledgers.AsNoTracking().Include(l => l.AccountGroup).FirstOrDefaultAsync(l => l.Id == r.PurposeLedgerId, ct)
                      ?? throw new DomainException("Choose what the donation is for.");
        if (purpose.AccountGroup?.Name != DonationsGroup) throw new DomainException("Choose a donation head from the Donations group.");

        long into;
        if (r.Mode == "Cash") into = (await systemLedgers.GetAsync(ct))(SystemLedger.Cash);
        else if (r.Mode == "Bank")
        {
            into = r.BankLedgerId ?? (await systemLedgers.GetAsync(ct))(SystemLedger.Bank);
            var bank = await db.Ledgers.AsNoTracking().Include(l => l.AccountGroup).FirstOrDefaultAsync(l => l.Id == into, ct);
            if (bank?.AccountGroup?.SystemCode != SystemGroup.BankAccounts) throw new DomainException("Choose the bank account the money went into.");
        }
        else throw new DomainException("Choose cash or bank / UPI.");

        await using var tx = await db.BeginAsync(ct);
        var narration = $"Donation from {name}{(string.IsNullOrWhiteSpace(r.HouseName) ? "" : ", " + r.HouseName.Trim())}";
        var voucher = await vouchers.SaveAsync(new VoucherDto
        {
            VoucherType = VoucherType.Receipt, Date = clock.Now, HeaderLedgerId = into, ReferenceNo = Cut(r.Reference, 100), Narration = Cut(narration, 250),
            Lines = [new VoucherLineDto { LedgerId = purpose.Id, Amount = r.Amount, Narration = Cut(narration, 250) }],
        }, null, ct);
        var d = new Donation
        {
            ReceiptNo = voucher.DocumentNo ?? "", Date = clock.Now, DonorName = name, HouseName = Cut(r.HouseName?.Trim(), 150), Phone = Cut(r.Phone?.Trim(), 30),
            Address = Cut(r.Address?.Trim(), 500), Star = r.Star, DevoteeId = r.DevoteeId, PurposeLedgerId = purpose.Id, Amount = r.Amount,
            ReceivedIntoLedgerId = into, Reference = Cut(r.Reference?.Trim(), 100), Pan = pan, VoucherId = voucher.Id, Notes = Cut(r.Notes?.Trim(), 500),
            UserName = user.UserName,
        };
        db.Donations.Add(d);
        audit.Log(Screens.Donation, "Add", $"Donation {d.ReceiptNo}: {name} ₹{r.Amount:0.00} for {purpose.Name}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(d.Id, ct);
    }

    public async Task<DonationDto> GetAsync(long id, CancellationToken ct) =>
        (await Project(db.Donations.AsNoTracking().Where(d => d.Id == id)).ToListAsync(ct)).Select(ToDto).FirstOrDefault() ?? throw new NotFoundException("Donation not found.");

    public async Task<PagedResult<DonationDto>> ListAsync(DateOnly? from, DateOnly? to, string? search, long? purposeId, int page, int pageSize, CancellationToken ct)
    {
        var q = db.Donations.AsNoTracking();
        if (from is { } f) { var start = clock.DayStart(f); q = q.Where(d => d.Date >= start); }
        if (to is { } t) { var end = clock.DayEnd(t); q = q.Where(d => d.Date < end); }
        if (purposeId is not null) q = q.Where(d => d.PurposeLedgerId == purposeId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(d => d.DonorName.Contains(s) || d.ReceiptNo.Contains(s) || (d.Phone != null && d.Phone.Contains(s)) || (d.HouseName != null && d.HouseName.Contains(s)));
        }
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 500);
        var rows = await Project(q.OrderByDescending(d => d.Date).Skip((Math.Max(page, 1) - 1) * size).Take(size)).ToListAsync(ct);
        return new PagedResult<DonationDto>(rows.Select(ToDto).ToList(), total, page, size);
    }

    /// <summary>Cancels a donation receipt (its voucher is cancelled, so cash and accounts are reversed).</summary>
    public async Task CancelAsync(long id, CancellationToken ct)
    {
        var d = await db.Donations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Donation not found.");
        if (d.Cancelled) throw new DomainException("This donation is already cancelled.");
        await using var tx = await db.BeginAsync(ct);
        await vouchers.CancelAsync(d.VoucherId, ct);
        d.Cancelled = true;
        audit.Log(Screens.Donation, "Cancel", $"Donation {d.ReceiptNo}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed record Row(Donation D, string Purpose, string Into);

    private IQueryable<Row> Project(IQueryable<Donation> q) =>
        q.Select(d => new Row(d, d.PurposeLedger!.Name,
            db.Ledgers.Where(l => l.Id == d.ReceivedIntoLedgerId).Select(l => l.Name).FirstOrDefault() ?? ""));

    private static DonationDto ToDto(Row x) => new(x.D.Id, x.D.ReceiptNo, x.D.Date, x.D.DonorName, x.D.HouseName, x.D.Phone, x.D.Address, x.D.Star,
        x.D.Star is { } s ? MalayalamNames.Star(s) : null, x.D.PurposeLedgerId, x.Purpose, x.D.Amount, x.Into, x.D.Reference, x.D.Pan, x.D.Notes,
        x.D.Cancelled, x.D.VoucherId, x.D.UserName);

    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
