namespace Kshethra.Domain.Calendar;

/// <summary>
/// Positions of the Sun and Moon for the panchangam, after Jean Meeus, "Astronomical Algorithms" (2nd ed.):
/// the Sun from chapter 25 (≈0.01°), the Moon from chapter 47 with the full 60-term longitude series (≈10″).
/// Longitudes are tropical (mean equinox of date); <see cref="Sidereal"/> subtracts the Lahiri ayanamsa used by
/// Kerala panchangams. The Moon moves ≈0.55° an hour, so a star change is placed to within a minute or two.
/// </summary>
public static class Astronomy
{
    private const double Deg = Math.PI / 180;

    /// <summary>Julian Day (UT) of an instant.</summary>
    public static double JulianDay(DateTimeOffset t) => 2440587.5 + t.ToUniversalTime().Ticks / (double)TimeSpan.TicksPerDay
        - new DateTime(1970, 1, 1).Ticks / (double)TimeSpan.TicksPerDay;

    public static DateTimeOffset FromJulianDay(double jd) =>
        new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks((long)Math.Round((jd - 2440587.5) * TimeSpan.TicksPerDay));

    /// <summary>ΔT = TT − UT in seconds (NASA polynomial for 2005–2050; adequate a few decades either side).</summary>
    public static double DeltaTSeconds(double jdUt)
    {
        double y = 2000 + (jdUt - 2451545.0) / 365.25;
        double t = y - 2000;
        return 62.92 + 0.32217 * t + 0.005589 * t * t;
    }

    /// <summary>Julian centuries of Terrestrial Time since J2000.0.</summary>
    private static double Centuries(double jdUt) => (jdUt + DeltaTSeconds(jdUt) / 86400.0 - 2451545.0) / 36525.0;

    private static double Norm(double a) { a %= 360; return a < 0 ? a + 360 : a; }

    /// <summary>Apparent geocentric longitude of the Sun (degrees, mean equinox of date, with aberration).</summary>
    public static double SunLongitude(double jdUt)
    {
        double T = Centuries(jdUt);
        double L0 = 280.46646 + 36000.76983 * T + 0.0003032 * T * T;
        double M = (357.52911 + 35999.05029 * T - 0.0001537 * T * T) * Deg;
        double C = (1.914602 - 0.004817 * T - 0.000014 * T * T) * Math.Sin(M)
                   + (0.019993 - 0.000101 * T) * Math.Sin(2 * M)
                   + 0.000289 * Math.Sin(3 * M);
        return Norm(L0 + C - 0.00569);
    }

    // Meeus table 47.A: multiples of D, M, M', F and the coefficient of the sine in 0.000001°.
    private static readonly (int D, int M, int Mp, int F, int L)[] MoonTerms =
    [
        (0, 0, 1, 0, 6288774), (2, 0, -1, 0, 1274027), (2, 0, 0, 0, 658314), (0, 0, 2, 0, 213618),
        (0, 1, 0, 0, -185116), (0, 0, 0, 2, -114332), (2, 0, -2, 0, 58793), (2, -1, -1, 0, 57066),
        (2, 0, 1, 0, 53322), (2, -1, 0, 0, 45758), (0, 1, -1, 0, -40923), (1, 0, 0, 0, -34720),
        (0, 1, 1, 0, -30383), (2, 0, 0, -2, 15327), (0, 0, 1, 2, -12528), (0, 0, 1, -2, 10980),
        (4, 0, -1, 0, 10675), (0, 0, 3, 0, 10034), (4, 0, -2, 0, 8548), (2, 1, -1, 0, -7888),
        (2, 1, 0, 0, -6766), (1, 0, -1, 0, -5163), (1, 1, 0, 0, 4987), (2, -1, 1, 0, 4036),
        (2, 0, 2, 0, 3994), (4, 0, 0, 0, 3861), (2, 0, -3, 0, 3665), (0, 1, -2, 0, -2689),
        (2, 0, -1, 2, -2602), (2, -1, -2, 0, 2390), (1, 0, 1, 0, -2348), (2, -2, 0, 0, 2236),
        (0, 1, 2, 0, -2120), (0, 2, 0, 0, -2069), (2, -2, -1, 0, 2048), (2, 0, 1, -2, -1773),
        (2, 0, 0, 2, -1595), (4, -1, -1, 0, 1215), (0, 0, 2, 2, -1110), (3, 0, -1, 0, -892),
        (2, 1, 1, 0, -810), (4, -1, -2, 0, 759), (0, 2, -1, 0, -713), (2, 2, -1, 0, -700),
        (2, 1, -2, 0, 691), (2, -1, 0, -2, 596), (4, 0, 1, 0, 549), (0, 0, 4, 0, 537),
        (4, -1, 0, 0, 520), (1, 0, -2, 0, -487), (2, 1, 0, -2, -399), (0, 0, 2, -2, -381),
        (1, 1, 1, 0, 351), (3, 0, -2, 0, -340), (4, 0, -3, 0, 330), (2, -1, 2, 0, 327),
        (0, 2, 1, 0, -323), (1, 1, -1, 0, 299), (2, 0, 3, 0, 294),
    ];

    /// <summary>Geocentric longitude of the Moon (degrees, mean equinox of date).</summary>
    public static double MoonLongitude(double jdUt)
    {
        double T = Centuries(jdUt), T2 = T * T, T3 = T2 * T, T4 = T3 * T;
        double Lp = 218.3164477 + 481267.88123421 * T - 0.0015786 * T2 + T3 / 538841 - T4 / 65194000;
        double D = 297.8501921 + 445267.1114034 * T - 0.0018819 * T2 + T3 / 545868 - T4 / 113065000;
        double M = 357.5291092 + 35999.0502909 * T - 0.0001536 * T2 + T3 / 24490000;
        double Mp = 134.9633964 + 477198.8675055 * T + 0.0087414 * T2 + T3 / 69699 - T4 / 14712000;
        double F = 93.2720950 + 483202.0175233 * T - 0.0036539 * T2 - T3 / 3526000 + T4 / 863310000;
        double A1 = 119.75 + 131.849 * T, A2 = 53.09 + 479264.290 * T;
        double E = 1 - 0.002516 * T - 0.0000074 * T2;

        double sum = 0;
        foreach (var (d, m, mp, f, l) in MoonTerms)
        {
            double coeff = l * (Math.Abs(m) == 1 ? E : Math.Abs(m) == 2 ? E * E : 1);
            sum += coeff * Math.Sin((d * D + m * M + mp * Mp + f * F) * Deg);
        }
        sum += 3958 * Math.Sin(A1 * Deg) + 1962 * Math.Sin((Lp - F) * Deg) + 318 * Math.Sin(A2 * Deg);
        return Norm(Lp + sum / 1_000_000);
    }

    /// <summary>Lahiri (Chitrapaksha) ayanamsa in degrees: 23°51′11″ at J2000, precessing ≈50.29″ a year.</summary>
    public static double LahiriAyanamsa(double jdUt)
    {
        double T = Centuries(jdUt);
        return 23.85306 + 1.396971 * T + 0.0003086 * T * T;
    }

    /// <summary>Sidereal (nirayana) longitude from a tropical one.</summary>
    public static double Sidereal(double tropical, double jdUt) => Norm(tropical - LahiriAyanamsa(jdUt));

    public static double SiderealSun(double jdUt) => Sidereal(SunLongitude(jdUt), jdUt);
    public static double SiderealMoon(double jdUt) => Sidereal(MoonLongitude(jdUt), jdUt);

    /// <summary>
    /// Sunrise or sunset (UT Julian Day) on the civil date whose local noon is near <paramref name="jdLocalNoon"/>.
    /// NOAA method: Sun's centre 0.833° below the horizon (refraction + semi-diameter). Iterated twice for accuracy.
    /// </summary>
    public static double SunEvent(double jdLocalNoon, double latitude, double longitude, bool rise)
    {
        double jd = jdLocalNoon + (rise ? -0.25 : 0.25);
        for (int i = 0; i < 3; i++)
        {
            double T = (jd - 2451545.0) / 36525.0;
            double L0 = Norm(280.46646 + 36000.76983 * T + 0.0003032 * T * T);
            double M = 357.52911 + 35999.05029 * T - 0.0001537 * T * T;
            double e = 0.016708634 - 0.000042037 * T - 0.0000001267 * T * T;
            double C = (1.914602 - 0.004817 * T - 0.000014 * T * T) * Math.Sin(M * Deg) + (0.019993 - 0.000101 * T) * Math.Sin(2 * M * Deg) + 0.000289 * Math.Sin(3 * M * Deg);
            double omega = 125.04 - 1934.136 * T;
            double lambda = L0 + C - 0.00569 - 0.00478 * Math.Sin(omega * Deg);
            double eps0 = 23 + (26 + (21.448 - T * (46.815 + T * (0.00059 - T * 0.001813))) / 60) / 60;
            double eps = eps0 + 0.00256 * Math.Cos(omega * Deg);
            double decl = Math.Asin(Math.Sin(eps * Deg) * Math.Sin(lambda * Deg)) / Deg;
            double y = Math.Tan(eps / 2 * Deg); y *= y;
            double eqTime = 4 / Deg * (y * Math.Sin(2 * L0 * Deg) - 2 * e * Math.Sin(M * Deg) + 4 * e * y * Math.Sin(M * Deg) * Math.Cos(2 * L0 * Deg)
                            - 0.5 * y * y * Math.Sin(4 * L0 * Deg) - 1.25 * e * e * Math.Sin(2 * M * Deg)); // minutes
            double cosH = (Math.Cos(90.833 * Deg) - Math.Sin(latitude * Deg) * Math.Sin(decl * Deg)) / (Math.Cos(latitude * Deg) * Math.Cos(decl * Deg));
            double h = Math.Acos(Math.Clamp(cosH, -1, 1)) / Deg;
            // Minutes after 0h UT of the UT date of jdLocalNoon.
            double minutes = 720 - 4 * (longitude + (rise ? h : -h)) - eqTime;
            double jd0 = Math.Floor(jdLocalNoon - 0.5) + 0.5;
            // Keep the event on the local civil day around jdLocalNoon (±12 h).
            double candidate = jd0 + minutes / 1440.0;
            while (candidate < jdLocalNoon - 0.75) candidate += 1;
            while (candidate > jdLocalNoon + 0.75) candidate -= 1;
            jd = candidate;
        }
        return jd;
    }

    /// <summary>
    /// First instant in [<paramref name="from"/>, <paramref name="to"/>] (UT JD) where the angle <paramref name="f"/>
    /// reaches <paramref name="target"/> while increasing. f must be continuous and increasing modulo 360 over the range.
    /// </summary>
    public static double? FindCrossing(Func<double, double> f, double target, double from, double to, double step = 0.25)
    {
        double Diff(double t) { var d = Norm(f(t) - target); return d > 180 ? d - 360 : d; } // −180…180, crosses 0 upward
        double a = from, da = Diff(a);
        for (double b = Math.Min(from + step, to); a < to; a = b, b = Math.Min(b + step, to))
        {
            double db = Diff(b);
            if (da < 0 && db >= 0 && db - da < 90)
            {
                double lo = a, hi = b;
                for (int i = 0; i < 40; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (Diff(mid) < 0) lo = mid; else hi = mid;
                }
                return (lo + hi) / 2;
            }
            da = db;
            if (b >= to) break;
        }
        return null;
    }
}
