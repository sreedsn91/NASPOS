using System.Collections.Concurrent;

namespace Kshethra.Domain.Calendar;

/// <summary>Where the temple is: panchangam times depend on local sunrise.</summary>
public sealed record TempleLocation(double Latitude, double Longitude, TimeSpan UtcOffset)
{
    /// <summary>Thrissur, Kerala (IST) — the default until the temple sets its own location.</summary>
    public static readonly TempleLocation Kerala = new(10.5276, 76.2144, TimeSpan.FromHours(5.5));
}

/// <summary>Names used across the app, in English and Malayalam.</summary>
public static class MalayalamNames
{
    /// <summary>The 27 nakshatras (stars), from Ashwathi.</summary>
    public static readonly (string En, string Ml)[] Stars =
    [
        ("Ashwathi", "അശ്വതി"), ("Bharani", "ഭരണി"), ("Karthika", "കാർത്തിക"), ("Rohini", "രോഹിണി"), ("Makayiram", "മകയിരം"),
        ("Thiruvathira", "തിരുവാതിര"), ("Punartham", "പുണർതം"), ("Pooyam", "പൂയം"), ("Ayilyam", "ആയില്യം"), ("Makam", "മകം"),
        ("Pooram", "പൂരം"), ("Uthram", "ഉത്രം"), ("Atham", "അത്തം"), ("Chithira", "ചിത്തിര"), ("Chothi", "ചോതി"),
        ("Vishakham", "വിശാഖം"), ("Anizham", "അനിഴം"), ("Thrikketta", "തൃക്കേട്ട"), ("Moolam", "മൂലം"), ("Pooradam", "പൂരാടം"),
        ("Uthradam", "ഉത്രാടം"), ("Thiruvonam", "തിരുവോണം"), ("Avittam", "അവിട്ടം"), ("Chathayam", "ചതയം"),
        ("Pooruruttathi", "പൂരുരുട്ടാതി"), ("Uthrattathi", "ഉത്രട്ടാതി"), ("Revathi", "രേവതി"),
    ];

    /// <summary>Malayalam months by the Sun's sidereal sign, from Medam (Mesha, sign 0).</summary>
    public static readonly (string En, string Ml)[] Months =
    [
        ("Medam", "മേടം"), ("Edavam", "ഇടവം"), ("Mithunam", "മിഥുനം"), ("Karkidakam", "കർക്കിടകം"), ("Chingam", "ചിങ്ങം"),
        ("Kanni", "കന്നി"), ("Thulam", "തുലാം"), ("Vrischikam", "വൃശ്ചികം"), ("Dhanu", "ധനു"), ("Makaram", "മകരം"),
        ("Kumbham", "കുംഭം"), ("Meenam", "മീനം"),
    ];

    /// <summary>Tithis 1–15 of a fortnight; 15 = Pournami (full moon) in the bright half, Amavasi (new moon) in the dark half.</summary>
    public static readonly (string En, string Ml)[] Tithis =
    [
        ("Prathama", "പ്രഥമ"), ("Dwitheeya", "ദ്വിതീയ"), ("Thritheeya", "തൃതീയ"), ("Chathurthi", "ചതുർത്ഥി"), ("Panchami", "പഞ്ചമി"),
        ("Shashti", "ഷഷ്ഠി"), ("Sapthami", "സപ്തമി"), ("Ashtami", "അഷ്ടമി"), ("Navami", "നവമി"), ("Dashami", "ദശമി"),
        ("Ekadashi", "ഏകാദശി"), ("Dwadashi", "ദ്വാദശി"), ("Thrayodashi", "ത്രയോദശി"), ("Chathurdashi", "ചതുർദ്ദശി"), ("Pournami", "പൗർണ്ണമി"),
    ];

    public static readonly (string En, string Ml)[] Weekdays =
    [
        ("Sunday", "ഞായർ"), ("Monday", "തിങ്കൾ"), ("Tuesday", "ചൊവ്വ"), ("Wednesday", "ബുധൻ"), ("Thursday", "വ്യാഴം"),
        ("Friday", "വെള്ളി"), ("Saturday", "ശനി"),
    ];

    /// <summary>Tithi 1–30 → name ("Shukla Ekadashi", "Pournami", "Amavasi" …).</summary>
    public static string TithiName(int tithi, bool malayalam = false)
    {
        if (tithi == 30) return malayalam ? "അമാവാസി" : "Amavasi";
        if (tithi == 15) return malayalam ? Tithis[14].Ml : Tithis[14].En;
        var n = Tithis[(tithi - 1) % 15];
        var bright = tithi <= 15;
        return malayalam ? $"{(bright ? "വെളുത്ത" : "കറുത്ത")} {n.Ml}" : $"{(bright ? "Shukla" : "Krishna")} {n.En}";
    }

    public static string Star(int star, bool malayalam = false) => malayalam ? Stars[star].Ml : Stars[star].En;
    public static string Month(int sign, bool malayalam = false) => malayalam ? Months[sign].Ml : Months[sign].En;
}

/// <summary>One civil day of the Kerala panchangam.</summary>
/// <param name="MonthSign">Malayalam month as the Sun's sign (0 = Medam … 4 = Chingam … 11 = Meenam).</param>
/// <param name="Star">The day's star (0 = Ashwathi … 26 = Revathi): the star 6 nazhika (2 h 24 min) after sunrise.</param>
/// <param name="ExtraStar">A short star that begins and ends between two of those moments; it also belongs to this day.</param>
/// <param name="Tithi">Tithi at sunrise, 1–30 (15 = Pournami, 30 = Amavasi).</param>
public sealed record PanchangamDay(
    DateOnly Date,
    int MalayalamYear,
    int MonthSign,
    int MalayalamDay,
    int Star,
    int? ExtraStar,
    int StarAtSunrise,
    DateTimeOffset StarAtSunriseEnds,
    int Tithi,
    DateTimeOffset TithiEnds,
    DateTimeOffset Sunrise,
    DateTimeOffset Sunset,
    DateTimeOffset? Sankranti,
    bool StarSameAsYesterday = false,
    int? ExtraTithi = null,
    bool TithiSameAsYesterday = false)
{
    public DayOfWeek Weekday => Date.DayOfWeek;
    /// <summary>
    /// The star day for offerings and birthdays: a star that is in force at the deciding moment on two days running
    /// belongs to the first of them (Kerala practice); a skipped short star belongs to the day it starts in.
    /// </summary>
    public bool IsStarDay(int star) => (Star == star && !StarSameAsYesterday) || ExtraStar == star;

    /// <summary>The day for a tithi observance (1–30), by the same rules as <see cref="IsStarDay"/> using sunrise.</summary>
    public bool IsTithiDay(int tithi) => (Tithi == tithi && !TithiSameAsYesterday) || ExtraTithi == tithi;
    public string MonthName => MalayalamNames.Month(MonthSign);
    public string StarName => MalayalamNames.Star(Star);
    public string TithiName => MalayalamNames.TithiName(Tithi);
    /// <summary>e.g. "Thulam 19, 1202".</summary>
    public string MalayalamDate => $"{MonthName} {MalayalamDay}, {MalayalamYear}";
    public bool HasStar(int star) => Star == star || ExtraStar == star;
}

/// <summary>
/// Kerala panchangam computed from the Sun and Moon (see <see cref="Astronomy"/>):
/// <list type="bullet">
/// <item>Day's star — the nakshatra in force 6 nazhika (2 h 24 min) after local sunrise, the usual Kerala rule for
/// birthdays and star-day offerings. A star that starts and ends between two such moments is credited to the day it
/// starts in, so no star is ever missed when scheduling "every Karthika".</item>
/// <item>Malayalam (Kollavarsham) month — begins with the Sun's entry into a sidereal sign (sankranti). If that happens
/// before 3/5 of the daytime has passed, the day is the 1st of the month; otherwise the next day is.</item>
/// <item>Kollavarsham year — changes on Chingam 1 (year = Gregorian year of that Chingam − 824).</item>
/// </list>
/// Results are cached per date and location. Temples can correct any day from their printed panchangam; corrections
/// are applied by the application layer on top of this.
/// </summary>
public static class Panchangam
{
    private const double Nazhika = 24.0 / 1440.0; // 24 minutes, in days
    private static readonly ConcurrentDictionary<(DateOnly, TempleLocation), PanchangamDay> Cache = new();

    public static PanchangamDay Day(DateOnly date, TempleLocation? location = null) =>
        Cache.GetOrAdd((date, location ?? TempleLocation.Kerala), k => Compute(k.Item1, k.Item2));

    public static IEnumerable<PanchangamDay> Range(DateOnly from, DateOnly to, TempleLocation? location = null)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return Day(d, location);
    }

    private static double LocalNoon(DateOnly d, TempleLocation loc) =>
        Astronomy.JulianDay(new DateTimeOffset(d.Year, d.Month, d.Day, 12, 0, 0, loc.UtcOffset));

    public static double SunriseJd(DateOnly d, TempleLocation loc) => Astronomy.SunEvent(LocalNoon(d, loc), loc.Latitude, loc.Longitude, rise: true);
    public static double SunsetJd(DateOnly d, TempleLocation loc) => Astronomy.SunEvent(LocalNoon(d, loc), loc.Latitude, loc.Longitude, rise: false);

    public static int StarAt(double jd) => (int)Math.Floor(Astronomy.SiderealMoon(jd) / (360.0 / 27)) % 27;

    public static int TithiAt(double jd)
    {
        var elong = Astronomy.MoonLongitude(jd) - Astronomy.SunLongitude(jd);
        elong = ((elong % 360) + 360) % 360;
        return (int)Math.Floor(elong / 12) + 1;
    }

    private static int SignAt(double jd) => (int)Math.Floor(Astronomy.SiderealSun(jd) / 30) % 12;

    /// <summary>When the star in force at <paramref name="jd"/> ends.</summary>
    public static double StarEnds(double jd)
    {
        var next = (StarAt(jd) + 1) % 27;
        return Astronomy.FindCrossing(Astronomy.SiderealMoon, next * 360.0 / 27, jd, jd + 1.6, 0.1) ?? jd + 1;
    }

    private static double TithiEnds(double jd)
    {
        var next = TithiAt(jd) % 30; // 0-based index of the next tithi boundary
        double Elong(double t) => Astronomy.MoonLongitude(t) - Astronomy.SunLongitude(t);
        return Astronomy.FindCrossing(Elong, next * 12.0, jd, jd + 1.6, 0.1) ?? jd + 1;
    }

    /// <summary>The moment, 6 nazhika after sunrise, that decides the day's star.</summary>
    private static double StarMoment(DateOnly d, TempleLocation loc) => SunriseJd(d, loc) + 6 * Nazhika;

    /// <summary>Cut-off for a sankranti to count for this day: 3/5 of the daytime after sunrise.</summary>
    private static double SankrantiCutoff(DateOnly d, TempleLocation loc)
    {
        double rise = SunriseJd(d, loc), set = SunsetJd(d, loc);
        return rise + (set - rise) * 3 / 5;
    }

    /// <summary>First day of the Malayalam month that begins with the Sun entering <paramref name="sign"/> on or before <paramref name="near"/>.</summary>
    private static (DateOnly Start, double SankrantiJd) MonthStart(int sign, DateOnly near, TempleLocation loc)
    {
        double to = LocalNoon(near, loc) + 1, from = to - 40;
        var t = Astronomy.FindCrossing(Astronomy.SiderealSun, sign * 30.0, from, to, 1.0)
                ?? throw new InvalidOperationException("Sankranti not found.");
        var local = Astronomy.FromJulianDay(t).ToOffset(loc.UtcOffset);
        var day = DateOnly.FromDateTime(local.Date);
        // Sun enters the sign at night (before sunrise) → it still counts for that civil day only if before the
        // day's cut-off, which also holds for the hours after midnight.
        var start = t <= SankrantiCutoff(day, loc) ? day : day.AddDays(1);
        return (start, t);
    }

    private static PanchangamDay Compute(DateOnly date, TempleLocation loc)
    {
        double rise = SunriseJd(date, loc), set = SunsetJd(date, loc);

        // Malayalam month and day.
        int sign = SignAt(SankrantiCutoff(date, loc));
        var (start, sankranti) = MonthStart(sign, date, loc);
        if (date < start)
        {
            sign = (sign + 11) % 12;
            (start, sankranti) = MonthStart(sign, start.AddDays(-2), loc);
        }
        int malDay = date.DayNumber - start.DayNumber + 1;
        int chingamYear = sign is >= 4 and <= 8 ? start.Year : start.Year - 1;
        int meYear = chingamYear - 824;

        // Star of the day, and a short star that would otherwise be skipped.
        double moment = StarMoment(date, loc);
        int star = StarAt(moment);
        int nextDayStar = StarAt(StarMoment(date.AddDays(1), loc));
        int? extra = (nextDayStar - star + 27) % 27 == 2 ? (star + 1) % 27 : null;
        bool sameAsYesterday = StarAt(StarMoment(date.AddDays(-1), loc)) == star;

        int sunriseStar = StarAt(rise);
        int tithi = TithiAt(rise);
        int nextDayTithi = TithiAt(SunriseJd(date.AddDays(1), loc));
        int? extraTithi = (nextDayTithi - tithi + 30) % 30 == 2 ? tithi % 30 + 1 : null;
        bool tithiSameAsYesterday = TithiAt(SunriseJd(date.AddDays(-1), loc)) == tithi;
        var sankrantiLocal = Astronomy.FromJulianDay(sankranti).ToOffset(loc.UtcOffset);

        return new PanchangamDay(
            date, meYear, sign, malDay, star, extra, sunriseStar,
            Astronomy.FromJulianDay(StarEnds(rise)).ToOffset(loc.UtcOffset),
            tithi,
            Astronomy.FromJulianDay(TithiEnds(rise)).ToOffset(loc.UtcOffset),
            Astronomy.FromJulianDay(rise).ToOffset(loc.UtcOffset),
            Astronomy.FromJulianDay(set).ToOffset(loc.UtcOffset),
            malDay == 1 ? sankrantiLocal : null,
            sameAsYesterday,
            extraTithi,
            tithiSameAsYesterday);
    }
}
