using FluentAssertions;
using Kshethra.Domain;
using Kshethra.Domain.Calendar;
using Kshethra.Domain.Temple;
using Xunit;

namespace Kshethra.Tests.Domain;

public class ScheduleRuleTests
{
    private static PanchangamDay Day(DateOnly d) => Panchangam.Day(d);
    private const int Karthika = 2, Thiruvonam = 21;

    [Fact]
    public void Once_gives_the_start_day_only()
    {
        new ScheduleRule(ScheduleKind.Once, new DateOnly(2026, 10, 9)).Dates(Day).Should().Equal(new DateOnly(2026, 10, 9));
    }

    [Fact]
    public void Every_friday_for_a_period()
    {
        var dates = new ScheduleRule(ScheduleKind.Weekly, new DateOnly(2026, 10, 5), new DateOnly(2026, 11, 1), Weekday: DayOfWeek.Friday).Dates(Day).ToList();
        dates.Should().Equal(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 30));
    }

    [Fact]
    public void Every_karthika_a_number_of_times_falls_on_karthika_days_about_a_month_apart()
    {
        var dates = new ScheduleRule(ScheduleKind.StarDay, new DateOnly(2026, 1, 1), Count: 12, Star: Karthika).Dates(Day).ToList();
        dates.Should().HaveCount(12);
        dates.Should().OnlyContain(d => Day(d).IsStarDay(Karthika));
        dates.Zip(dates.Skip(1), (a, b) => b.DayNumber - a.DayNumber).Should().OnlyContain(gap => gap >= 25 && gap <= 29);
    }

    [Fact]
    public void Star_day_in_one_malayalam_month_is_once_a_year()
    {
        // Thiruvonam of Chingam is Onam: 26 Aug 2026 (Thiruvonam 2026).
        var dates = new ScheduleRule(ScheduleKind.StarDay, new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31), Star: Thiruvonam, MonthSign: 4).Dates(Day).ToList();
        dates.Should().HaveCount(2);
        dates.Should().OnlyContain(d => Day(d).MonthSign == 4);
    }

    [Fact]
    public void Malayalam_first_is_the_month_start()
    {
        var dates = new ScheduleRule(ScheduleKind.MalayalamDate, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), MalayalamDay: 1).Dates(Day).ToList();
        dates.Should().HaveCount(12);
        dates.Should().Contain(new DateOnly(2026, 4, 14)); // Medam 1 (Vishu)
        dates.Should().Contain(new DateOnly(2026, 8, 17)); // Chingam 1
    }

    [Fact]
    public void Pournami_only_is_monthly_and_both_fortnights_is_twice_as_often()
    {
        var start = new DateOnly(2026, 1, 1);
        var full = new ScheduleRule(ScheduleKind.Tithi, start, new DateOnly(2026, 12, 31), Tithi: 15, Paksha: Paksha.Shukla).Dates(Day).ToList();
        var both = new ScheduleRule(ScheduleKind.Tithi, start, new DateOnly(2026, 12, 31), Tithi: 15).Dates(Day).ToList();
        full.Count.Should().BeInRange(12, 13);
        both.Count.Should().BeInRange(24, 26);
        full.Should().OnlyContain(d => Day(d).IsTithiDay(15));
    }

    [Theory]
    [InlineData(ScheduleKind.Weekly, "Choose the weekday.")]
    [InlineData(ScheduleKind.StarDay, "Choose the star.")]
    [InlineData(ScheduleKind.Tithi, "Choose the tithi.")]
    public void Missing_details_are_reported(ScheduleKind kind, string problem)
    {
        new ScheduleRule(kind, new DateOnly(2026, 1, 1), Count: 3).Problem().Should().Be(problem);
    }

    [Fact]
    public void Repeating_needs_an_end_and_is_limited()
    {
        var start = new DateOnly(2026, 1, 1);
        new ScheduleRule(ScheduleKind.Daily, start).Problem().Should().NotBeNull();
        new ScheduleRule(ScheduleKind.Daily, start, start.AddYears(6)).Problem().Should().Contain("at most");
        new ScheduleRule(ScheduleKind.Daily, start, start.AddDays(-1)).Problem().Should().Contain("before");
        var act = () => new ScheduleRule(ScheduleKind.Daily, start).Dates(Day).ToList();
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Describe_reads_naturally()
    {
        new ScheduleRule(ScheduleKind.Weekly, new DateOnly(2026, 10, 9), new DateOnly(2026, 12, 25), Weekday: DayOfWeek.Friday).Describe()
            .Should().Be("Every Friday, 09-10-2026 to 25-12-2026");
        new ScheduleRule(ScheduleKind.StarDay, new DateOnly(2026, 10, 9), Count: 12, Star: Karthika).Describe()
            .Should().Be("Every Karthika star day, from 09-10-2026, 12 times");
    }
}
