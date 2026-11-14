using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Application.Documents;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Temple;

namespace Kshethra.Application.Temple;

public sealed record ScheduleDto(ScheduleKind Kind, DateOnly Start, DateOnly? End = null, int? Count = null, DayOfWeek? Weekday = null,
    int? Star = null, int? MalayalamDay = null, int? MonthSign = null, int? Tithi = null, Paksha Paksha = Paksha.Both)
{
    public ScheduleRule ToRule() => new(Kind, Start, End, Count, Weekday, Star, MalayalamDay, MonthSign, Tithi, Paksha);
}

public sealed record BookingLineInput(long ProductId, long? DeityId, long? PoojaSlotId, long? DevoteeId, string? DevoteeName, int? Star,
    decimal Qty, decimal? Rate, ScheduleDto Schedule, string? Remarks);

/// <param name="PaymentMode">"Cash", "Card" (card / UPI) or "Credit" (needs <see cref="PartyLedgerId"/>).</param>
public sealed record BookingRequest(long? DevoteeId, string? ContactName, string? Phone, string? Address, string PaymentMode,
    string? PaymentReference, decimal? Tendered, long? PartyLedgerId, string? Notes, bool SaveDevotee, List<BookingLineInput> Lines);

public sealed record PreviewDate(DateOnly Date, string Weekday, string MalayalamDate, string Star, string Tithi);
public sealed record SchedulePreview(string Description, int Count, IReadOnlyList<PreviewDate> Dates, decimal? Amount);

public sealed record BookingLineDto(long Id, int LineNo, long ProductId, string Offering, string? OfferingMl, string? Deity, string? PoojaSlot,
    string DevoteeName, int? Star, string? StarName, decimal Qty, decimal Rate, decimal Amount, string Schedule, int Occurrences, int Done,
    int Cancelled, DateOnly? NextDate, IReadOnlyList<DateOnly> Dates);

public sealed record BookingDto(long Id, string BookingNo, DateTimeOffset BookedAt, string ContactName, string? Phone, string? Address,
    long? DevoteeId, decimal Total, string Status, long DocumentId, string? Notes, string? UserName, IReadOnlyList<BookingLineDto> Lines);

public sealed record BookingListItem(long Id, string BookingNo, DateTimeOffset BookedAt, string ContactName, string? Phone, decimal Total,
    string Status, string Offerings, int Occurrences, int Done, DateOnly? NextDate);

/// <summary>
/// Vazhipadu bookings. Saving a booking works out the dates of each offering from the temple calendar, raises the
/// counter bill (so cash, accounts and sales reports work as for any sale) and records each occurrence for the
/// priest's daily list — all in one transaction.
/// </summary>
public class BookingService(IAppDbContext db, ICurrentUser user, IClock clock, DocumentService documents,
    TempleCalendarService calendar, AuditService audit)
{
    private async Task<List<DateOnly>> DatesAsync(ScheduleRule rule, CancellationToken ct)
    {
        var to = rule.End ?? rule.Start.AddYears(ScheduleRule.MaxYears);
        var resolve = await calendar.ResolverAsync(rule.Start, to, ct);
        return rule.Dates(resolve).ToList();
    }

    /// <summary>Shows the dates a schedule gives, before booking (and the amount when a rate is given).</summary>
    public async Task<SchedulePreview> PreviewAsync(ScheduleDto schedule, decimal? rate, decimal qty, CancellationToken ct)
    {
        var rule = schedule.ToRule();
        if (rule.Problem() is { } p) throw new DomainException(p, "schedule_invalid");
        var dates = await DatesAsync(rule, ct);
        var resolve = await calendar.ResolverAsync(dates.FirstOrDefault(rule.Start), dates.LastOrDefault(rule.Start), ct);
        var shown = dates.Take(120).Select(d =>
        {
            var p = resolve(d);
            return new PreviewDate(d, d.DayOfWeek.ToString(), p.MalayalamDate, MalayalamNames.Star(p.Star), MalayalamNames.TithiName(p.Tithi));
        }).ToList();
        return new SchedulePreview(rule.Describe(), dates.Count, shown, rate is { } r ? Math.Round(r * (qty <= 0 ? 1 : qty) * dates.Count, 2) : null);
    }

    public async Task<BookingDto> CreateAsync(BookingRequest r, CancellationToken ct)
    {
        if (r.Lines is not { Count: > 0 }) throw new DomainException("Add at least one offering.");
        if (r.Lines.Count > 100) throw new DomainException("At most 100 offerings per receipt.");
        var productIds = r.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var deities = await db.Deities.AsNoTracking().Select(d => d.Id).ToListAsync(ct);
        var slots = await db.PoojaSlots.AsNoTracking().Select(s => s.Id).ToListAsync(ct);
        Devotee? devotee = r.DevoteeId is { } did ? await db.Devotees.FirstOrDefaultAsync(d => d.Id == did, ct) ?? throw new DomainException("Devotee not found.") : null;

        // Work out every line: who, which star, which dates, how much.
        var planned = new List<(BookingLineInput In, Product P, string Name, int? Star, ScheduleRule Rule, List<DateOnly> Dates, decimal Rate, decimal Qty)>();
        int n = 0;
        foreach (var l in r.Lines)
        {
            n++;
            if (!products.TryGetValue(l.ProductId, out var p) || !p.IsActive) throw new DomainException($"Line {n}: offering not found.");
            var name = (l.DevoteeName ?? devotee?.Name ?? "").Trim();
            if (name.Length > 150) name = name[..150];
            int? star = l.Star ?? (l.DevoteeName is null ? devotee?.Star : null);
            if (p.NeedsName && name.Length == 0) throw new DomainException($"Line {n} ({p.Name}): enter the devotee's name.");
            if (p.NeedsStar && star is null) throw new DomainException($"Line {n} ({p.Name}): choose the devotee's star.");
            if (star is not null and not (>= 0 and < 27)) throw new DomainException($"Line {n}: invalid star.");
            if (l.DeityId is { } dId && !deities.Contains(dId)) throw new DomainException($"Line {n}: deity not found.");
            if (l.PoojaSlotId is { } sId && !slots.Contains(sId)) throw new DomainException($"Line {n}: pooja time not found.");
            var qty = l.Qty <= 0 ? 1 : l.Qty;
            if (qty > 10000) throw new DomainException($"Line {n}: quantity is too large.");
            var rate = l.Rate ?? p.SaleRate;
            if (rate < 0) throw new DomainException($"Line {n}: rate cannot be negative.");
            var rule = l.Schedule.ToRule();
            if (rule.Problem() is { } problem) throw new DomainException($"Line {n} ({p.Name}): {problem}", "schedule_invalid");
            if (rule.Start < clock.Today().AddDays(-1)) throw new DomainException($"Line {n} ({p.Name}): the offering date is in the past.");
            var dates = await DatesAsync(rule, ct);
            if (dates.Count == 0) throw new DomainException($"Line {n} ({p.Name}): no day in that period matches ({rule.Describe()}).");
            planned.Add((l, p, name, star, rule, dates, rate, qty));
        }

        var contact = (r.ContactName ?? devotee?.Name ?? planned[0].Name).Trim();
        if (contact.Length == 0) contact = "Devotee";
        if (contact.Length > 150) contact = contact[..150];
        var phone = string.IsNullOrWhiteSpace(r.Phone) ? devotee?.Phone : r.Phone.Trim();

        await using var tx = await db.BeginAsync(ct);

        if (devotee is null && r.SaveDevotee && planned[0].Name.Length > 0)
        {
            devotee = db.Devotees.Add(new Devotee { Name = planned[0].Name, Star = planned[0].Star, Phone = phone, Address = r.Address?.Trim() }).Entity;
            await db.SaveChangesAsync(ct);
        }

        // The counter bill: one line per offering line, quantity = times × quantity each time.
        var doc = new TradeDocumentDto
        {
            VoucherType = VoucherType.POSSales, Date = clock.Now, PartyName = contact, PartyPhone = phone, PartyAddress = r.Address,
            PartyLedgerId = r.PaymentMode == "Credit" ? r.PartyLedgerId : null,
            Remarks = r.Notes, PaymentReference = r.PaymentReference,
            Lines = planned.Select(x => new TradeLineDto
            {
                ProductId = x.P.Id, Qty = x.Qty * x.Dates.Count, Rate = x.Rate, TaxInclusive = true,
                Remarks = Cut($"{x.Name}{(x.Star is { } s ? " · " + MalayalamNames.Star(s) : "")} · {x.Rule.Describe()}", 250),
            }).ToList(),
        };
        switch (r.PaymentMode)
        {
            case "Cash":
                doc.PaymentMode = PaymentMode.Cash;
                break;
            case "Card":
                doc.PaymentMode = PaymentMode.Mixed;
                var preview = await documents.PreviewAsync(doc, ct);
                doc.CardAmount = preview.BillAmount;
                break;
            case "Credit":
                if (r.PartyLedgerId is null) throw new DomainException("Choose the account for a credit booking.");
                doc.PaymentMode = PaymentMode.Credit;
                break;
            default:
                throw new DomainException("Choose how the devotee paid: cash, card / UPI or credit.");
        }
        var saved = await documents.SaveAsync(doc, null, ct);
        if (r.PaymentMode == "Cash" && r.Tendered is { } tendered && tendered >= saved.BillAmount)
            await db.TradeDocuments.Where(d => d.Id == saved.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.TenderedAmount, tendered), ct);

        var booking = new Booking
        {
            BookingNo = saved.DocumentNo ?? "", BookedAt = clock.Now, DevoteeId = devotee?.Id, ContactName = contact, Phone = phone,
            Address = Cut(r.Address, 500), DocumentId = saved.Id, Total = saved.BillAmount, Notes = Cut(r.Notes, 1000), UserName = user.UserName,
        };
        int lineNo = 0;
        foreach (var x in planned)
        {
            var p = x.P;
            booking.Lines.Add(new BookingLine
            {
                LineNo = ++lineNo, ProductId = p.Id, DeityId = x.In.DeityId ?? p.DeityId, PoojaSlotId = x.In.PoojaSlotId ?? p.PoojaSlotId,
                DevoteeId = x.In.DevoteeId ?? (x.In.DevoteeName is null ? devotee?.Id : null), DevoteeName = x.Name, Star = x.Star,
                Qty = x.Qty, Rate = x.Rate, Amount = Math.Round(x.Rate * x.Qty * x.Dates.Count, 2),
                ScheduleKind = x.Rule.Kind, StartDate = x.Rule.Start, EndDate = x.Rule.End, MaxCount = x.Rule.Count, Weekday = x.Rule.Weekday,
                ScheduleStar = x.Rule.Star, MalayalamDay = x.Rule.MalayalamDay, MonthSign = x.Rule.MonthSign, Tithi = x.Rule.Tithi, Paksha = x.Rule.Paksha,
                Occurrences = x.Dates.Count, ScheduleText = Cut(x.Rule.Describe(), 300)!, Remarks = Cut(x.In.Remarks, 300),
            });
        }
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(ct);

        foreach (var (line, x) in booking.Lines.Zip(planned))
            foreach (var d in x.Dates)
                db.BookingOccurrences.Add(new BookingOccurrence { BookingId = booking.Id, BookingLineId = line.Id, Date = d });
        audit.Log(Screens.Vazhipadu, "Add", $"Booking {booking.BookingNo}: {planned.Count} offering(s), {planned.Sum(p => p.Dates.Count)} day(s), ₹{booking.Total:0.00}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(booking.Id, ct);
    }

    public async Task<BookingDto> GetAsync(long id, CancellationToken ct)
    {
        var b = await db.Bookings.AsNoTracking().Include(x => x.Lines).ThenInclude(l => l.Product).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Booking not found.");
        var occ = await db.BookingOccurrences.AsNoTracking().Where(o => o.BookingId == id).ToListAsync(ct);
        var deities = await db.Deities.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var slots = await db.PoojaSlots.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var today = clock.Today();
        var lines = b.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var mine = occ.Where(o => o.BookingLineId == l.Id).OrderBy(o => o.Date).ToList();
            return new BookingLineDto(l.Id, l.LineNo, l.ProductId, l.Product?.Name ?? "", l.Product?.RegionalName,
                l.DeityId is { } d ? deities.GetValueOrDefault(d) : null, l.PoojaSlotId is { } s ? slots.GetValueOrDefault(s) : null,
                l.DevoteeName, l.Star, l.Star is { } st ? MalayalamNames.Star(st) : null, l.Qty, l.Rate, l.Amount, l.ScheduleText, l.Occurrences,
                mine.Count(o => o.Status == OccurrenceStatus.Done), mine.Count(o => o.Status == OccurrenceStatus.Cancelled),
                mine.FirstOrDefault(o => o.Status == OccurrenceStatus.Scheduled && o.Date >= today)?.Date, mine.Select(o => o.Date).ToList());
        }).ToList();
        return new BookingDto(b.Id, b.BookingNo, b.BookedAt, b.ContactName, b.Phone, b.Address, b.DevoteeId, b.Total, b.Status.ToString(),
            b.DocumentId, b.Notes, b.UserName, lines);
    }

    public async Task<PagedResult<BookingListItem>> ListAsync(DateOnly? from, DateOnly? to, string? search, long? devoteeId, bool? active,
        int page, int pageSize, CancellationToken ct)
    {
        var q = db.Bookings.AsNoTracking().AsQueryable();
        if (from is { } f) { var start = clock.DayStart(f); q = q.Where(b => b.BookedAt >= start); }
        if (to is { } t) { var end = clock.DayEnd(t); q = q.Where(b => b.BookedAt < end); }
        if (devoteeId is not null) q = q.Where(b => b.DevoteeId == devoteeId || b.Lines.Any(l => l.DevoteeId == devoteeId));
        if (active == true) q = q.Where(b => b.Status == BookingStatus.Active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(b => b.BookingNo.Contains(s) || b.ContactName.Contains(s) || (b.Phone != null && b.Phone.Contains(s))
                             || b.Lines.Any(l => l.DevoteeName.Contains(s)));
        }
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 500);
        var today = clock.Today();
        var rows = await q.OrderByDescending(b => b.BookedAt).Skip((Math.Max(page, 1) - 1) * size).Take(size)
            .Select(b => new
            {
                b.Id, b.BookingNo, b.BookedAt, b.ContactName, b.Phone, b.Total, b.Status,
                Offerings = b.Lines.OrderBy(l => l.LineNo).Select(l => l.Product!.Name).ToList(),
                Occ = db.BookingOccurrences.Where(o => o.BookingId == b.Id).Select(o => new { o.Date, o.Status }).ToList(),
            }).ToListAsync(ct);
        return new PagedResult<BookingListItem>(rows.Select(x => new BookingListItem(x.Id, x.BookingNo, x.BookedAt, x.ContactName, x.Phone, x.Total,
            x.Status.ToString(), string.Join(", ", x.Offerings.Distinct()), x.Occ.Count, x.Occ.Count(o => o.Status == OccurrenceStatus.Done),
            x.Occ.Where(o => o.Status == OccurrenceStatus.Scheduled && o.Date >= today).OrderBy(o => o.Date).Select(o => (DateOnly?)o.Date).FirstOrDefault())).ToList(),
            total, page, size);
    }

    /// <summary>
    /// Cancels a booking before any of it has been performed: its bill is cancelled (stock, cash and accounts reversed)
    /// and every day is removed from the pooja list. Once an offering has been done, refund with a sales return instead.
    /// </summary>
    public async Task CancelAsync(long id, string? reason, CancellationToken ct)
    {
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Booking not found.");
        if (b.Status == BookingStatus.Cancelled) throw new DomainException("This booking is already cancelled.");
        if (await db.BookingOccurrences.AnyAsync(o => o.BookingId == id && o.Status == OccurrenceStatus.Done, ct))
            throw new DomainException("Some of these offerings have already been performed. Stop the remaining days instead, and refund with a sales return if needed.", "booking_started");
        await using var tx = await db.BeginAsync(ct);
        await documents.CancelAsync(b.DocumentId, string.IsNullOrWhiteSpace(reason) ? "Booking cancelled" : reason, ct);
        b.Status = BookingStatus.Cancelled;
        await db.BookingOccurrences.Where(o => o.BookingId == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OccurrenceStatus.Cancelled), ct);
        audit.Log(Screens.Vazhipadu, "Cancel", $"Booking {b.BookingNo}: {reason}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Stops the remaining (future, not yet done) days of one offering line — e.g. the devotee asked to stop.</summary>
    public async Task<int> StopLineAsync(long lineId, DateOnly from, CancellationToken ct)
    {
        var line = await db.BookingLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct) ?? throw new NotFoundException("Offering not found.");
        if (!await db.Bookings.AnyAsync(b => b.Id == line.BookingId, ct)) throw new NotFoundException("Booking not found.");
        var n = await db.BookingOccurrences.Where(o => o.BookingLineId == lineId && o.Status == OccurrenceStatus.Scheduled && o.Date >= from)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OccurrenceStatus.Cancelled), ct);
        audit.Log(Screens.Vazhipadu, "Edit", $"Booking line {lineId}: {n} remaining day(s) stopped from {from:dd-MM-yyyy}");
        await db.SaveChangesAsync(ct);
        return n;
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
