using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Temple;

public sealed record PoojaListRow(long OccurrenceId, long BookingId, string BookingNo, string Offering, string? OfferingMl, string? Deity,
    string? PoojaSlot, TimeOnly? SlotTime, string DevoteeName, int? Star, string? StarName, string? StarNameMl, decimal Qty, string Status,
    string? Remarks, string? Phone, string? DoneBy);

public sealed record OfferingCount(string Offering, string? OfferingMl, decimal Qty, int Count);

public sealed record PoojaListDay(DateOnly Date, CalendarDayDto Calendar, IReadOnlyList<PoojaListRow> Rows, IReadOnlyList<OfferingCount> Summary);

public sealed record MarkRequest(List<long> OccurrenceIds, bool Done);

/// <summary>The day's offerings for the priests and kitchen: who, which star, which offering, at which pooja.</summary>
public class PoojaListService(IAppDbContext db, ICurrentUser user, IClock clock, TempleCalendarService calendar, AuditService audit)
{
    public async Task<PoojaListDay> DayAsync(DateOnly date, long? deityId, long? poojaSlotId, bool includeCancelled, CancellationToken ct)
    {
        var deities = await db.Deities.AsNoTracking().ToDictionaryAsync(d => d.Id, ct);
        var slots = await db.PoojaSlots.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        var q = db.BookingOccurrences.AsNoTracking().Where(o => o.Date == date);
        if (!includeCancelled) q = q.Where(o => o.Status != OccurrenceStatus.Cancelled);
        var rows = await q.Select(o => new
        {
            o.Id, o.BookingId, o.Status, o.DoneBy,
            Line = o.Line!,
            Product = o.Line!.Product!,
            Booking = db.Bookings.Where(b => b.Id == o.BookingId).Select(b => new { b.BookingNo, b.Phone }).First(),
        }).ToListAsync(ct);

        var list = rows
            .Where(r => deityId is null || r.Line.DeityId == deityId)
            .Where(r => poojaSlotId is null || r.Line.PoojaSlotId == poojaSlotId)
            .Select(r =>
            {
                var slot = r.Line.PoojaSlotId is { } s ? slots.GetValueOrDefault(s) : null;
                var deity = r.Line.DeityId is { } d ? deities.GetValueOrDefault(d) : null;
                return new PoojaListRow(r.Id, r.BookingId, r.Booking.BookingNo, r.Product.Name, r.Product.RegionalName, deity?.Name, slot?.Name, slot?.Time,
                    r.Line.DevoteeName, r.Line.Star, r.Line.Star is { } st ? MalayalamNames.Star(st) : null, r.Line.Star is { } st2 ? MalayalamNames.Star(st2, true) : null,
                    r.Line.Qty, r.Status.ToString(), r.Line.Remarks, r.Booking.Phone, r.DoneBy);
            })
            .OrderBy(r => r.SlotTime ?? TimeOnly.MaxValue).ThenBy(r => r.PoojaSlot).ThenBy(r => r.Deity).ThenBy(r => r.Offering).ThenBy(r => r.DevoteeName)
            .ToList();

        var summary = list.Where(r => r.Status != nameof(OccurrenceStatus.Cancelled))
            .GroupBy(r => (r.Offering, r.OfferingMl)).Select(g => new OfferingCount(g.Key.Offering, g.Key.OfferingMl, g.Sum(x => x.Qty), g.Count()))
            .OrderBy(x => x.Offering).ToList();
        return new PoojaListDay(date, await calendar.DayAsync(date, ct), list, summary);
    }

    /// <summary>Marks offerings performed (or back to scheduled). Cancelled days cannot be marked.</summary>
    public async Task<int> MarkAsync(MarkRequest r, CancellationToken ct)
    {
        if (r.OccurrenceIds is not { Count: > 0 }) return 0;
        if (r.OccurrenceIds.Count > 2000) throw new DomainException("Too many rows at once.");
        var rows = await db.BookingOccurrences.Where(o => r.OccurrenceIds.Contains(o.Id) && o.Status != OccurrenceStatus.Cancelled).ToListAsync(ct);
        if (rows.Any(o => o.Date > clock.Today()) && r.Done) throw new DomainException("Offerings for a future day can't be marked as done yet.");
        foreach (var o in rows)
        {
            o.Status = r.Done ? OccurrenceStatus.Done : OccurrenceStatus.Scheduled;
            o.DoneAt = r.Done ? clock.Now : null;
            o.DoneBy = r.Done ? user.UserName : null;
        }
        audit.Log(Screens.PoojaList, "Edit", $"{rows.Count} offering(s) marked {(r.Done ? "done" : "not done")}");
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
