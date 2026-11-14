using Kshethra.Domain.Calendar;

namespace Kshethra.Domain.Temple;

/// <summary>How often an offering is to be performed.</summary>
public enum ScheduleKind
{
    /// <summary>One day.</summary>
    Once = 1,
    /// <summary>Every day in the period.</summary>
    Daily = 2,
    /// <summary>Every given weekday (e.g. every Friday).</summary>
    Weekly = 3,
    /// <summary>Every day of a star (e.g. every Karthika), optionally only in one Malayalam month.</summary>
    StarDay = 4,
    /// <summary>A Malayalam date every month (e.g. the 1st — sankranti), optionally only in one month.</summary>
    MalayalamDate = 5,
    /// <summary>A tithi (e.g. every Ekadashi, Pournami, Amavasi, Shashti).</summary>
    Tithi = 6,
}

/// <summary>Bright or dark fortnight, for tithi schedules.</summary>
public enum Paksha { Both = 0, Shukla = 1, Krishna = 2 }

/// <summary>
/// When an offering repeats. The period is <see cref="Start"/> to <see cref="End"/> and/or at most <see cref="Count"/>
/// occurrences — at least one of them must be set for repeating kinds.
/// </summary>
public sealed record ScheduleRule(
    ScheduleKind Kind,
    DateOnly Start,
    DateOnly? End = null,
    int? Count = null,
    DayOfWeek? Weekday = null,
    int? Star = null,
    int? MalayalamDay = null,
    int? MonthSign = null,
    int? Tithi = null,
    Paksha Paksha = Paksha.Both)
{
    /// <summary>Longest period searched for repeating offerings.</summary>
    public const int MaxYears = 5;
    /// <summary>Most occurrences one booking line may have.</summary>
    public const int MaxOccurrences = 400;

    /// <summary>Checks the rule makes sense; returns a user-facing problem or null.</summary>
    public string? Problem()
    {
        if (End is { } e && e < Start) return "The end date is before the start date.";
        if (Count is < 1) return "The number of times must be at least 1.";
        if (Count > MaxOccurrences) return $"At most {MaxOccurrences} times per offering.";
        if (Kind != ScheduleKind.Once && End is null && Count is null) return "Give an end date or the number of times.";
        if (End is { } end && end > Start.AddYears(MaxYears)) return $"A booking can run for at most {MaxYears} years.";
        return Kind switch
        {
            ScheduleKind.Weekly when Weekday is null => "Choose the weekday.",
            ScheduleKind.StarDay when Star is not (>= 0 and < 27) => "Choose the star.",
            ScheduleKind.MalayalamDate when MalayalamDay is not (>= 1 and <= 32) => "Choose the Malayalam date (1–32).",
            ScheduleKind.Tithi when Tithi is not (>= 1 and <= 15) => "Choose the tithi.",
            _ when MonthSign is not null and not (>= 0 and < 12) => "Choose a valid Malayalam month.",
            _ => null,
        };
    }

    private bool Matches(PanchangamDay d) => Kind switch
    {
        ScheduleKind.Once or ScheduleKind.Daily => true,
        ScheduleKind.Weekly => d.Weekday == Weekday,
        ScheduleKind.StarDay => d.IsStarDay(Star!.Value) && (MonthSign is null || d.MonthSign == MonthSign),
        ScheduleKind.MalayalamDate => d.MalayalamDay == MalayalamDay && (MonthSign is null || d.MonthSign == MonthSign),
        ScheduleKind.Tithi => (Paksha != Paksha.Krishna && d.IsTithiDay(Tithi!.Value))
                              || (Paksha != Paksha.Shukla && d.IsTithiDay(Tithi!.Value + 15)),
        _ => false,
    };

    /// <summary>The dates the offering falls on, using the (corrected) panchangam supplied by <paramref name="day"/>.</summary>
    public IEnumerable<DateOnly> Dates(Func<DateOnly, PanchangamDay> day)
    {
        if (Problem() is { } p) throw new DomainException(p, "schedule_invalid");
        if (Kind == ScheduleKind.Once) { yield return Start; yield break; }
        var last = End ?? Start.AddYears(MaxYears);
        int found = 0;
        for (var d = Start; d <= last && found < (Count ?? MaxOccurrences); d = d.AddDays(1))
        {
            if (!Matches(day(d))) continue;
            found++;
            yield return d;
        }
    }

    /// <summary>Human description, e.g. "Every Friday, 10-10-2026 to 26-12-2026" or "Every Karthika, 12 times".</summary>
    public string Describe()
    {
        string what = Kind switch
        {
            ScheduleKind.Once => $"On {Start:dd-MM-yyyy}",
            ScheduleKind.Daily => "Every day",
            ScheduleKind.Weekly => $"Every {Weekday}",
            ScheduleKind.StarDay => $"Every {MalayalamNames.Star(Star ?? 0)} star day",
            ScheduleKind.MalayalamDate => MalayalamDay == 1 ? "Every Malayalam month 1st (sankranti)" : $"Every Malayalam date {MalayalamDay}",
            ScheduleKind.Tithi => Tithi == 15
                ? Paksha switch { Paksha.Shukla => "Every Pournami", Paksha.Krishna => "Every Amavasi", _ => "Every Pournami and Amavasi" }
                : $"Every {(Paksha switch { Paksha.Shukla => "Shukla ", Paksha.Krishna => "Krishna ", _ => "" })}{MalayalamNames.Tithis[(Tithi ?? 1) - 1].En}",
            _ => Kind.ToString(),
        };
        if (Kind == ScheduleKind.Once) return what;
        if (MonthSign is { } m && Kind is ScheduleKind.StarDay or ScheduleKind.MalayalamDate) what += $" of {MalayalamNames.Month(m)}";
        var period = End is { } e ? $"{Start:dd-MM-yyyy} to {e:dd-MM-yyyy}" : $"from {Start:dd-MM-yyyy}";
        return Count is { } c ? $"{what}, {period}, {c} time{(c == 1 ? "" : "s")}" : $"{what}, {period}";
    }
}
