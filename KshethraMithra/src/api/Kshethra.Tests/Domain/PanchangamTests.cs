using FluentAssertions;
using Kshethra.Domain.Calendar;

namespace Kshethra.Tests.Domain;

public class PanchangamTests
{
    private static double JdTd(int y, int m, int d) =>
        Astronomy.JulianDay(new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Moon_longitude_matches_Meeus_example_47a()
    {
        // 1992 April 12, 0h TD: λ = 133.162655° (Meeus, Astronomical Algorithms, example 47.a).
        var jdTd = JdTd(1992, 4, 12);
        var jdUt = jdTd - Astronomy.DeltaTSeconds(jdTd) / 86400.0;
        Astronomy.MoonLongitude(jdUt).Should().BeApproximately(133.162655, 0.002);
    }

    [Fact]
    public void Sun_longitude_matches_Meeus_example_25a()
    {
        // 1992 October 13, 0h TD: true longitude 199.90988°; minus aberration 0.00569° (nutation not applied).
        var jdTd = JdTd(1992, 10, 13);
        var jdUt = jdTd - Astronomy.DeltaTSeconds(jdTd) / 86400.0;
        Astronomy.SunLongitude(jdUt).Should().BeApproximately(199.90988 - 0.00569, 0.002);
    }

    [Theory]
    [InlineData("2025-08-17", 4, 1, 1201)]  // Chingam 1, 1201 (Malayalam New Year)
    [InlineData("2026-04-14", 0, 1, 1201)]  // Medam 1 — Vishu
    [InlineData("2025-04-14", 0, 1, 1200)]  // Medam 1, 1200 — Vishu
    [InlineData("2026-08-17", 4, 1, 1202)]  // Chingam 1, 1202
    [InlineData("2025-08-16", 3, 31, 1200)] // last day of Karkidakam 1200 (began 17 July 2025)
    public void Malayalam_month_starts_on_the_right_day(string date, int sign, int day, int year)
    {
        var p = Panchangam.Day(DateOnly.Parse(date));
        p.MonthSign.Should().Be(sign, p.MalayalamDate);
        p.MalayalamDay.Should().Be(day, p.MalayalamDate);
        p.MalayalamYear.Should().Be(year);
    }

    [Theory]
    [InlineData("2025-09-05", 21)] // Thiruvonam — Onam 2025
    [InlineData("2026-08-26", 21)] // Thiruvonam — Onam 2026
    public void Day_star_matches_known_festival_days(string date, int star)
    {
        var p = Panchangam.Day(DateOnly.Parse(date));
        p.Star.Should().Be(star, $"{date} is {MalayalamNames.Star(p.Star)} by calculation");
    }

    [Fact]
    public void Full_moon_day_is_Pournami()
    {
        // Full moon 7 September 2025, 18:09 UT (23:39 IST): Pournami at sunrise that day.
        Panchangam.Day(new DateOnly(2025, 9, 7)).Tithi.Should().Be(15);
        // New moon 21 September 2025, 19:54 UT: Amavasi at sunrise on the 21st.
        Panchangam.Day(new DateOnly(2025, 9, 21)).Tithi.Should().Be(30);
    }

    [Fact]
    public void Every_star_has_exactly_one_star_day_per_lunar_month()
    {
        // Over a year each of the 27 stars must come round 13–14 times, never twice in a row and never skipped.
        var days = Panchangam.Range(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ToList();
        for (int star = 0; star < 27; star++)
        {
            var hits = days.Where(d => d.IsStarDay(star)).Select(d => d.Date).ToList();
            hits.Count.Should().BeInRange(13, 14, MalayalamNames.Star(star));
            hits.Zip(hits.Skip(1), (a, b) => b.DayNumber - a.DayNumber).Should().OnlyContain(gap => gap >= 25 && gap <= 29, MalayalamNames.Star(star));
        }
    }

    [Fact]
    public void Sunrise_in_Kerala_is_early_morning_IST()
    {
        var p = Panchangam.Day(new DateOnly(2026, 10, 5));
        p.Sunrise.Offset.Should().Be(TimeSpan.FromHours(5.5));
        p.Sunrise.TimeOfDay.Should().BeGreaterThan(TimeSpan.FromHours(6)).And.BeLessThan(TimeSpan.FromHours(6.5));
        p.Sunset.TimeOfDay.Should().BeGreaterThan(TimeSpan.FromHours(18)).And.BeLessThan(TimeSpan.FromHours(18.5));
    }
}
