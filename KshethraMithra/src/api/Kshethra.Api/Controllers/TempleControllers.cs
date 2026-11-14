using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kshethra.Api.Infrastructure;
using Kshethra.Api.Security;
using Kshethra.Application.Common;
using Kshethra.Application.Temple;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;

namespace Kshethra.Api.Controllers;

/// <summary>Malayalam calendar (panchangam) for the temple's location, with the temple's corrections.</summary>
[ApiController, Authorize]
[Route("api/calendar")]
public class CalendarController(TempleCalendarService calendar) : ControllerBase
{
    /// <summary>Days from <paramref name="from"/> to <paramref name="to"/> (at most 400). Every signed-in user may read it.</summary>
    [HttpGet]
    public Task<List<CalendarDayDto>> Range([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) => calendar.RangeAsync(from, to, ct);

    [HttpGet("day/{date}")]
    public Task<CalendarDayDto> Day(DateOnly date, CancellationToken ct) => calendar.DayAsync(date, ct);

    [HttpGet("stars/{star:int}")]
    public Task<List<CalendarDayDto>> StarDays(int star, [FromQuery] DateOnly from, CancellationToken ct, [FromQuery] int count = 12) =>
        calendar.StarDaysAsync(star, from, count, ct);

    [HttpPut("corrections"), Permission(Screens.TempleCalendar, Right.Edit)]
    public async Task<IActionResult> Correct(CalendarCorrectionDto dto, CancellationToken ct) { await calendar.SaveCorrectionAsync(dto, ct); return NoContent(); }

    /// <summary>Names for pickers: stars, months, tithis, weekdays (English and Malayalam).</summary>
    [HttpGet("names")]
    public object Names() => new
    {
        stars = MalayalamNames.Stars.Select((s, i) => new { id = i, en = s.En, ml = s.Ml }),
        months = MalayalamNames.Months.Select((s, i) => new { id = i, en = s.En, ml = s.Ml }),
        tithis = MalayalamNames.Tithis.Select((s, i) => new { id = i + 1, en = s.En, ml = s.Ml }),
        weekdays = MalayalamNames.Weekdays.Select((s, i) => new { id = i, en = s.En, ml = s.Ml }),
    };
}

/// <summary>Deities, pooja times, devotees and the offerings list.</summary>
[ApiController, Authorize]
[Route("api/temple")]
public class TempleMastersController(TempleMastersService masters) : ControllerBase
{
    [HttpGet("deities")]
    public Task<List<DeityDto>> Deities([FromQuery] bool activeOnly, CancellationToken ct) => masters.DeitiesAsync(activeOnly, ct);

    [HttpPost("deities"), Permission(Screens.TempleMasters, Right.Add)]
    public async Task<object> AddDeity(DeityDto dto, CancellationToken ct) => new { id = await masters.SaveDeityAsync(dto with { Id = 0 }, ct) };

    [HttpPut("deities/{id:long}"), Permission(Screens.TempleMasters, Right.Edit)]
    public async Task<object> EditDeity(long id, DeityDto dto, CancellationToken ct) => new { id = await masters.SaveDeityAsync(dto with { Id = id }, ct) };

    [HttpGet("pooja-slots")]
    public Task<List<PoojaSlotDto>> Slots([FromQuery] bool activeOnly, CancellationToken ct) => masters.PoojaSlotsAsync(activeOnly, ct);

    [HttpPost("pooja-slots"), Permission(Screens.TempleMasters, Right.Add)]
    public async Task<object> AddSlot(PoojaSlotDto dto, CancellationToken ct) => new { id = await masters.SavePoojaSlotAsync(dto with { Id = 0 }, ct) };

    [HttpPut("pooja-slots/{id:long}"), Permission(Screens.TempleMasters, Right.Edit)]
    public async Task<object> EditSlot(long id, PoojaSlotDto dto, CancellationToken ct) => new { id = await masters.SavePoojaSlotAsync(dto with { Id = id }, ct) };

    [HttpGet("offerings"), Permission(Screens.Vazhipadu, Right.Read)]
    public Task<List<OfferingDto>> Offerings(CancellationToken ct) => masters.OfferingsAsync(ct);

    [HttpGet("devotees"), Permission(Screens.Devotee, Right.Read)]
    public Task<PagedResult<DevoteeDto>> Devotees([FromQuery] string? search, [FromQuery] int? star, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => masters.DevoteesAsync(search, star, page, pageSize, ct);

    [HttpGet("devotees/{id:long}"), Permission(Screens.Devotee, Right.Read)]
    public Task<DevoteeDto> Devotee(long id, CancellationToken ct) => masters.DevoteeAsync(id, ct);

    [HttpPost("devotees"), Permission(Screens.Devotee, Right.Add)]
    public async Task<object> AddDevotee(DevoteeDto dto, CancellationToken ct) => new { id = await masters.SaveDevoteeAsync(dto with { Id = 0 }, ct) };

    [HttpPut("devotees/{id:long}"), Permission(Screens.Devotee, Right.Edit)]
    public async Task<object> EditDevotee(long id, DevoteeDto dto, CancellationToken ct) => new { id = await masters.SaveDevoteeAsync(dto with { Id = id }, ct) };
}

public sealed record PreviewRequest(ScheduleDto Schedule, decimal? Rate, decimal Qty);
public sealed record StopLineRequest(DateOnly From);

/// <summary>Vazhipadu bookings, the daily pooja list and donations.</summary>
[ApiController, Authorize]
[Route("api/vazhipadu")]
public class VazhipaduController(BookingService bookings, PoojaListService poojaList, TemplePrintService print) : ControllerBase
{
    [HttpPost("preview"), Permission(Screens.Vazhipadu, Right.Read)]
    public Task<SchedulePreview> Preview(PreviewRequest r, CancellationToken ct) => bookings.PreviewAsync(r.Schedule, r.Rate, r.Qty, ct);

    [HttpPost("bookings"), Permission(Screens.Vazhipadu, Right.Add)]
    public Task<BookingDto> Book(BookingRequest r, CancellationToken ct) => bookings.CreateAsync(r, ct);

    [HttpGet("bookings"), Permission(Screens.Vazhipadu, Right.Read)]
    public Task<PagedResult<BookingListItem>> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? search,
        [FromQuery] long? devoteeId, [FromQuery] bool? active, CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        bookings.ListAsync(from, to, search, devoteeId, active, page, pageSize, ct);

    [HttpGet("bookings/{id:long}"), Permission(Screens.Vazhipadu, Right.Read)]
    public Task<BookingDto> Get(long id, CancellationToken ct) => bookings.GetAsync(id, ct);

    [HttpGet("bookings/{id:long}/receipt"), Permission(Screens.Vazhipadu, Right.Print)]
    public async Task<IActionResult> Receipt(long id, CancellationToken ct, [FromQuery] PrintFormat? paper = null, [FromQuery] bool? slips = null,
        [FromQuery] int? duplicates = null) =>
        this.Pdf(await print.BookingReceiptAsync(id, paper, slips, duplicates, ct), $"vazhipadu-{id}.pdf");

    /// <summary>Prints the receipt, duplicates and item slips straight to the server's receipt printer, cut after each.</summary>
    [HttpPost("bookings/{id:long}/print"), Permission(Screens.Vazhipadu, Right.Print)]
    public async Task<IActionResult> PrintDirect(long id, CancellationToken ct, [FromQuery] bool? slips = null, [FromQuery] int? duplicates = null)
    {
        await print.PrintBookingDirectAsync(id, slips, duplicates, ct);
        return NoContent();
    }

    /// <summary>One offering's cut slip on its own (lineNo from 1), printed as a separate job so the printer cuts after it.</summary>
    [HttpGet("bookings/{id:long}/slips/{lineNo:int}"), Permission(Screens.Vazhipadu, Right.Print)]
    public async Task<IActionResult> Slip(long id, int lineNo, CancellationToken ct, [FromQuery] PrintFormat? paper = null) =>
        this.Pdf(await print.BookingSlipAsync(id, lineNo, paper, ct), $"vazhipadu-{id}-slip-{lineNo}.pdf");

    [HttpPost("bookings/{id:long}/cancel"), Permission(Screens.Vazhipadu, Right.Cancel), RequireStepUp]
    public async Task<IActionResult> Cancel(long id, CancelBookingRequest r, CancellationToken ct) { await bookings.CancelAsync(id, r.Reason, ct); return NoContent(); }

    [HttpPost("lines/{lineId:long}/stop"), Permission(Screens.Vazhipadu, Right.Edit)]
    public async Task<object> Stop(long lineId, StopLineRequest r, CancellationToken ct) => new { stopped = await bookings.StopLineAsync(lineId, r.From, ct) };

    [HttpGet("pooja-list/{date}"), Permission(Screens.PoojaList, Right.Read)]
    public Task<PoojaListDay> PoojaList(DateOnly date, [FromQuery] long? deityId, [FromQuery] long? slotId, CancellationToken ct, [FromQuery] bool includeCancelled = false) =>
        poojaList.DayAsync(date, deityId, slotId, includeCancelled, ct);

    [HttpGet("pooja-list/{date}/pdf"), Permission(Screens.PoojaList, Right.Print)]
    public async Task<IActionResult> PoojaListPdf(DateOnly date, [FromQuery] long? deityId, [FromQuery] long? slotId, CancellationToken ct) =>
        this.Pdf(await print.PoojaListAsync(date, deityId, slotId, ct), $"pooja-list-{date:yyyyMMdd}.pdf");

    [HttpPost("pooja-list/mark"), Permission(Screens.PoojaList, Right.Edit)]
    public async Task<object> Mark(MarkRequest r, CancellationToken ct) => new { updated = await poojaList.MarkAsync(r, ct) };
}

public sealed record CancelBookingRequest(string? Reason);

[ApiController, Authorize]
[Route("api/donations")]
public class DonationsController(DonationService donations, TemplePrintService print) : ControllerBase
{
    [HttpGet("purposes"), Permission(Screens.Donation, Right.Read)]
    public Task<List<DonationPurpose>> Purposes(CancellationToken ct) => donations.PurposesAsync(ct);

    [HttpPost, Permission(Screens.Donation, Right.Add)]
    public Task<DonationDto> Create(DonationRequest r, CancellationToken ct) => donations.CreateAsync(r, ct);

    [HttpGet, Permission(Screens.Donation, Right.Read)]
    public Task<PagedResult<DonationDto>> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? search, [FromQuery] long? purposeId,
        CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) => donations.ListAsync(from, to, search, purposeId, page, pageSize, ct);

    [HttpGet("{id:long}"), Permission(Screens.Donation, Right.Read)]
    public Task<DonationDto> Get(long id, CancellationToken ct) => donations.GetAsync(id, ct);

    [HttpGet("{id:long}/receipt"), Permission(Screens.Donation, Right.Print)]
    public async Task<IActionResult> Receipt(long id, CancellationToken ct, [FromQuery] PrintFormat? paper = null) =>
        this.Pdf(await print.DonationReceiptAsync(id, paper, ct), $"donation-{id}.pdf");

    [HttpPost("{id:long}/cancel"), Permission(Screens.Donation, Right.Cancel), RequireStepUp]
    public async Task<IActionResult> Cancel(long id, CancellationToken ct) { await donations.CancelAsync(id, ct); return NoContent(); }
}
