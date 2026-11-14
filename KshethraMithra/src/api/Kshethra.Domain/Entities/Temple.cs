using Kshethra.Domain.Temple;

namespace Kshethra.Domain.Entities;

/// <summary>A deity (prathishta) of the temple — offerings are made to a deity.</summary>
public class Deity : TenantEntity
{
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A pooja time of the day (Usha pooja, Ucha pooja, Athazha pooja…) when offerings are performed.</summary>
public class PoojaSlot : TenantEntity
{
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    public TimeOnly? Time { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A regular devotee, kept so bookings can be made quickly and birthday (star) offerings remembered.</summary>
public class Devotee : TenantEntity
{
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    /// <summary>Birth star (nakshatram) 0–26, from Ashwathi.</summary>
    public int? Star { get; set; }
    public string? HouseName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum BookingStatus { Active = 1, Cancelled = 2 }
public enum OccurrenceStatus { Scheduled = 1, Done = 2, Cancelled = 3 }

/// <summary>
/// A vazhipadu booking: one receipt (the counter bill, <see cref="DocumentId"/>) for one or more offerings, each
/// with the devotee's name and star and when it is to be performed.
/// </summary>
public class Booking : TenantEntity
{
    /// <summary>Receipt number — the number of the counter bill.</summary>
    public string BookingNo { get; set; } = "";
    public DateTimeOffset BookedAt { get; set; }
    public long? DevoteeId { get; set; }
    public string ContactName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public long DocumentId { get; set; }
    public decimal Total { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Active;
    public string? Notes { get; set; }
    public string? UserName { get; set; }
    public List<BookingLine> Lines { get; set; } = [];
}

/// <summary>One offering on a booking: who it is for and when it is performed (once or repeating).</summary>
public class BookingLine : Entity
{
    public long BookingId { get; set; }
    public int LineNo { get; set; }
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public long? DeityId { get; set; }
    public long? PoojaSlotId { get; set; }
    public long? DevoteeId { get; set; }
    public string DevoteeName { get; set; } = "";
    public int? Star { get; set; }
    /// <summary>Quantity each time (e.g. 2 litres of payasam).</summary>
    public decimal Qty { get; set; } = 1;
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }

    public ScheduleKind ScheduleKind { get; set; } = ScheduleKind.Once;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? MaxCount { get; set; }
    public DayOfWeek? Weekday { get; set; }
    public int? ScheduleStar { get; set; }
    public int? MalayalamDay { get; set; }
    public int? MonthSign { get; set; }
    public int? Tithi { get; set; }
    public Paksha Paksha { get; set; }
    public int Occurrences { get; set; }
    public string ScheduleText { get; set; } = "";
    public string? Remarks { get; set; }

    public ScheduleRule Rule() => new(ScheduleKind, StartDate, EndDate, MaxCount, Weekday, ScheduleStar, MalayalamDay, MonthSign, Tithi, Paksha);
}

/// <summary>One day an offering is to be performed — what the priest's daily list is made of.</summary>
public class BookingOccurrence : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long BookingId { get; set; }
    public long BookingLineId { get; set; }
    public BookingLine? Line { get; set; }
    public DateOnly Date { get; set; }
    public OccurrenceStatus Status { get; set; } = OccurrenceStatus.Scheduled;
    public DateTimeOffset? DoneAt { get; set; }
    public string? DoneBy { get; set; }
}

/// <summary>A day of the panchangam corrected by the temple from its printed calendar (wins over the calculation).</summary>
public class CalendarCorrection : TenantEntity
{
    public DateOnly Date { get; set; }
    public int? MonthSign { get; set; }
    public int? MalayalamDay { get; set; }
    public int? Star { get; set; }
    public int? Tithi { get; set; }
    /// <summary>Festival or note shown on the calendar (e.g. "Thrikarthika", "Utsavam kodiyettam").</summary>
    public string? Festival { get; set; }
}

/// <summary>A donation (kanikka / sambhavana), posted as a receipt voucher to the chosen donation head.</summary>
public class Donation : TenantEntity
{
    public string ReceiptNo { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string DonorName { get; set; } = "";
    public string? HouseName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public int? Star { get; set; }
    public long? DevoteeId { get; set; }
    /// <summary>Income ledger of the purpose (General donation, Annadanam, Renovation fund…).</summary>
    public long PurposeLedgerId { get; set; }
    public Ledger? PurposeLedger { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Cash or bank ledger the money was received into.</summary>
    public long ReceivedIntoLedgerId { get; set; }
    public string? Reference { get; set; }
    public string? Pan { get; set; }
    public long VoucherId { get; set; }
    public string? Notes { get; set; }
    public bool Cancelled { get; set; }
    public string? UserName { get; set; }
}
