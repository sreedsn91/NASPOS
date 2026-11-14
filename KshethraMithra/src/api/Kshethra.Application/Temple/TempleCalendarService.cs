using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Temple;

public sealed record CalendarDayDto(
    DateOnly Date, string Weekday, string WeekdayMl,
    int MalayalamYear, int MonthSign, string Month, string MonthMl, int MalayalamDay, string MalayalamDate,
    int Star, string StarName, string StarNameMl, int? ExtraStar, string? ExtraStarName,
    int StarAtSunrise, string StarAtSunriseName, string StarAtSunriseEnds,
    int Tithi, string TithiName, string TithiNameMl, string TithiEnds,
    string Sunrise, string Sunset, string? Sankranti, string? Festival, bool Corrected, IReadOnlyList<string> Highlights);

public sealed record CalendarCorrectionDto(DateOnly Date, int? MonthSign, int? MalayalamDay, int? Star, int? Tithi, string? Festival);

/// <summary>
/// The temple's Malayalam calendar: the calculated panchangam (<see cref="Panchangam"/>) for the temple's location,
/// with the temple's own corrections from its printed panchangam laid on top. Bookings are scheduled from this, so a
/// corrected star also moves the offerings booked for it.
/// </summary>
public class TempleCalendarService(IAppDbContext db, SettingsService settings, IClock clock, AuditService audit)
{
    public async Task<TempleLocation> LocationAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var lat = s.TempleLatitude is >= -60 and <= 60 and not 0 ? s.TempleLatitude : TempleLocation.Kerala.Latitude;
        var lon = s.TempleLongitude is >= -180 and <= 180 and not 0 ? s.TempleLongitude : TempleLocation.Kerala.Longitude;
        return new TempleLocation(Math.Round(lat, 4), Math.Round(lon, 4), clock.Offset);
    }

    /// <summary>A day resolver for a date range: calculation + corrections. Loads corrections once.</summary>
    public async Task<Func<DateOnly, PanchangamDay>> ResolverAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var loc = await LocationAsync(ct);
        var fixes = await db.CalendarCorrections.AsNoTracking().Where(c => c.Date >= from.AddDays(-1) && c.Date <= to.AddDays(1))
            .ToDictionaryAsync(c => c.Date, ct);
        return d => Apply(Panchangam.Day(d, loc), fixes.GetValueOrDefault(d));
    }

    public static PanchangamDay Apply(PanchangamDay p, CalendarCorrection? c)
    {
        if (c is null) return p;
        return p with
        {
            MonthSign = c.MonthSign ?? p.MonthSign,
            MalayalamDay = c.MalayalamDay ?? p.MalayalamDay,
            Star = c.Star ?? p.Star,
            ExtraStar = c.Star is null ? p.ExtraStar : null,
            StarSameAsYesterday = c.Star is null && p.StarSameAsYesterday,
            Tithi = c.Tithi ?? p.Tithi,
            ExtraTithi = c.Tithi is null ? p.ExtraTithi : null,
            TithiSameAsYesterday = c.Tithi is null && p.TithiSameAsYesterday,
        };
    }

    public async Task<List<CalendarDayDto>> RangeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 400) throw new DomainException("Choose a range of at most 400 days.");
        var resolve = await ResolverAsync(from, to, ct);
        var fixes = await db.CalendarCorrections.AsNoTracking().Where(c => c.Date >= from && c.Date <= to).ToDictionaryAsync(c => c.Date, ct);
        var list = new List<CalendarDayDto>();
        for (var d = from; d <= to; d = d.AddDays(1)) list.Add(ToDto(resolve(d), fixes.GetValueOrDefault(d)));
        return list;
    }

    public async Task<CalendarDayDto> DayAsync(DateOnly date, CancellationToken ct) => (await RangeAsync(date, date, ct))[0];

    /// <summary>The next days of a star (e.g. the next 12 Karthika days) — for booking and for devotees' birthdays.</summary>
    public async Task<List<CalendarDayDto>> StarDaysAsync(int star, DateOnly from, int count, CancellationToken ct)
    {
        if (star is < 0 or > 26) throw new DomainException("Choose a star.");
        count = Math.Clamp(count, 1, 40);
        var to = from.AddDays(count * 28 + 30);
        var resolve = await ResolverAsync(from, to, ct);
        var fixes = await db.CalendarCorrections.AsNoTracking().Where(c => c.Date >= from && c.Date <= to).ToDictionaryAsync(c => c.Date, ct);
        var list = new List<CalendarDayDto>();
        for (var d = from; d <= to && list.Count < count; d = d.AddDays(1))
        {
            var p = resolve(d);
            if (p.IsStarDay(star)) list.Add(ToDto(p, fixes.GetValueOrDefault(d)));
        }
        return list;
    }

    public async Task SaveCorrectionAsync(CalendarCorrectionDto dto, CancellationToken ct)
    {
        if (dto.MonthSign is not null and not (>= 0 and < 12)) throw new DomainException("Choose a valid Malayalam month.");
        if (dto.MalayalamDay is not null and not (>= 1 and <= 32)) throw new DomainException("Malayalam date must be 1–32.");
        if (dto.Star is not null and not (>= 0 and < 27)) throw new DomainException("Choose a valid star.");
        if (dto.Tithi is not null and not (>= 1 and <= 30)) throw new DomainException("Choose a valid tithi.");
        var festival = string.IsNullOrWhiteSpace(dto.Festival) ? null : dto.Festival.Trim();
        if (festival?.Length > 200) throw new DomainException("Festival / note is too long.");

        var row = await db.CalendarCorrections.FirstOrDefaultAsync(c => c.Date == dto.Date, ct);
        bool empty = dto.MonthSign is null && dto.MalayalamDay is null && dto.Star is null && dto.Tithi is null && festival is null;
        if (empty)
        {
            if (row is not null) db.CalendarCorrections.Remove(row);
        }
        else
        {
            row ??= db.CalendarCorrections.Add(new CalendarCorrection { Date = dto.Date }).Entity;
            row.MonthSign = dto.MonthSign; row.MalayalamDay = dto.MalayalamDay; row.Star = dto.Star; row.Tithi = dto.Tithi; row.Festival = festival;
        }
        audit.Log(Screens.TempleCalendar, "Edit", $"Calendar {dto.Date:dd-MM-yyyy}: {(empty ? "correction removed" : "corrected")}");
        await db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<string> Highlights(PanchangamDay p)
    {
        var h = new List<string>();
        if (p.MalayalamDay == 1) h.Add($"{p.MonthName} sankranti");
        if (p.IsTithiDay(15)) h.Add("Pournami");
        if (p.IsTithiDay(30)) h.Add("Amavasi");
        if (p.IsTithiDay(11) || p.IsTithiDay(26)) h.Add("Ekadashi");
        if (p.IsTithiDay(6) || p.IsTithiDay(21)) h.Add("Shashti");
        if (p.IsTithiDay(13) || p.IsTithiDay(28)) h.Add("Pradosham");
        if (p.IsStarDay(21)) h.Add("Thiruvonam");
        return h;
    }

    public static CalendarDayDto ToDto(PanchangamDay p, CalendarCorrection? fix)
    {
        static string T(DateTimeOffset t) => t.ToString("HH:mm");
        static string Tw(DateTimeOffset t, DateOnly day) => DateOnly.FromDateTime(t.Date) == day ? t.ToString("HH:mm") : t.ToString("dd-MM HH:mm");
        var wd = MalayalamNames.Weekdays[(int)p.Date.DayOfWeek];
        return new CalendarDayDto(
            p.Date, wd.En, wd.Ml,
            p.MalayalamYear, p.MonthSign, MalayalamNames.Month(p.MonthSign), MalayalamNames.Month(p.MonthSign, true), p.MalayalamDay, p.MalayalamDate,
            p.Star, MalayalamNames.Star(p.Star), MalayalamNames.Star(p.Star, true), p.ExtraStar, p.ExtraStar is { } x ? MalayalamNames.Star(x) : null,
            p.StarAtSunrise, MalayalamNames.Star(p.StarAtSunrise), Tw(p.StarAtSunriseEnds, p.Date),
            p.Tithi, MalayalamNames.TithiName(p.Tithi), MalayalamNames.TithiName(p.Tithi, true), Tw(p.TithiEnds, p.Date),
            T(p.Sunrise), T(p.Sunset), p.Sankranti?.ToString("dd-MM HH:mm"), fix?.Festival,
            fix is not null && (fix.Star is not null || fix.Tithi is not null || fix.MonthSign is not null || fix.MalayalamDay is not null),
            Highlights(p));
    }
}
