using System.Net;
using System.Text.Json;
using FluentAssertions;
using Kshethra.Domain.Licensing;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Kshethra.Tests.Integration;

[CollectionDefinition("temple")]
public class TempleCollection : ICollectionFixture<ApiFixture>;

/// <summary>The temple counter on a real database: offerings booked on calendar days, the pooja list, donations, cancelling.</summary>
[Collection("temple")]
public class TempleTests(ApiFixture fx)
{
    private const string AdminPassword = "Admin@12345";
    private const string Temple = "Sree Krishna Temple";

    private static long Id(JsonElement e) => e.GetProperty("id").GetInt64();
    /// <summary>Walks an ESC/POS job (reset, raster bands, cuts) and counts the feed-and-cut commands; fails on anything else.</summary>
    private static int CountCuts(byte[] job)
    {
        int i = 0, cuts = 0;
        job[..2].Should().Equal(new byte[] { 0x1B, 0x40 });
        i = 2;
        while (i < job.Length)
        {
            if (job[i] == 0x1D && job[i + 1] == 0x76 && job[i + 2] == 0x30)
            {
                int widthBytes = job[i + 4] | job[i + 5] << 8, rows = job[i + 6] | job[i + 7] << 8;
                widthBytes.Should().Be(72, "a 72 mm page at 203 dpi is 576 dots");
                i += 8 + widthBytes * rows;
            }
            else if (job[i] == 0x1D && job[i + 1] == 0x56 && job[i + 2] == 0x42) { cuts++; i += 4; }
            else throw new InvalidOperationException($"Unexpected byte 0x{job[i]:X2} at {i}.");
        }
        return cuts;
    }

    private static int PdfPages(byte[] pdf) =>
        System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![a-zA-Z])").Count;

    private static async Task<decimal> TrialDifference(HttpClient c)
    {
        var tb = await c.Post("/api/reports/trial-balance", new { }).Ok<JsonElement>();
        var summary = tb.GetProperty("summary").EnumerateArray().First(s => s.GetProperty("key").GetString() == "Difference");
        return decimal.Parse(summary.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Temple_counter_books_offerings_on_calendar_days_and_keeps_books_balanced()
    {
        var c = fx.NewClient();
        object Setup(string key) => new
        {
            companyName = Temple, stateCode = "32", address = "Temple Road", city = "Guruvayur",
            adminUserName = "admin", adminPassword = AdminPassword, adminDisplayName = "Admin", licenseKey = key,
        };
        // A key issued for the shop software (NAS POS) does not unlock the temple software.
        using (var k = TestLicense.SigningKey())
        {
            var shopKey = LicenseKey.Create(k, Temple, null, null, DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today), null, product: "");
            (await c.Post("/api/auth/setup", Setup(shopKey))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        var setup = await c.Post("/api/auth/setup", Setup(TestLicense.Key(Temple))).Ok<JsonElement>();
        c.Bearer(setup.GetProperty("accessToken").GetString()!);

        // --- Seeded masters ---
        var offerings = await c.GetAsync("/api/temple/offerings").Ok<JsonElement>();
        offerings.GetArrayLength().Should().BeGreaterThanOrEqualTo(10);
        var archana = offerings.EnumerateArray().First(o => o.GetProperty("name").GetString() == "Archana");
        long archanaId = Id(archana);
        archana.GetProperty("rate").GetDecimal().Should().Be(30);
        archana.GetProperty("deityName").GetString().Should().Be("Main Deity");
        long payasam = Id(offerings.EnumerateArray().First(o => o.GetProperty("name").GetString() == "Palpayasam"));
        (await c.GetAsync("/api/temple/deities").Ok<JsonElement>()).GetArrayLength().Should().Be(5);
        (await c.GetAsync("/api/temple/pooja-slots").Ok<JsonElement>()).GetArrayLength().Should().Be(5);
        var purposes = await c.GetAsync("/api/donations/purposes").Ok<JsonElement>();
        purposes.EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Contain(["Annadanam", "Hundi Collection"]);

        // --- Calendar ---
        var chingam1 = await c.GetAsync("/api/calendar/day/2026-08-17").Ok<JsonElement>();
        chingam1.GetProperty("month").GetString().Should().Be("Chingam");
        chingam1.GetProperty("malayalamDay").GetInt32().Should().Be(1);
        chingam1.GetProperty("malayalamYear").GetInt32().Should().Be(1202);
        (await c.GetAsync("/api/calendar?from=2026-10-01&to=2026-10-31").Ok<JsonElement>()).GetArrayLength().Should().Be(31);

        // --- Booking: Archana every Friday for four weeks + one Palpayasam next Karthika ---
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(1);
        var everyFriday = new { kind = "Weekly", start, end = start.AddDays(27), weekday = "Friday" };
        var preview = await c.Post("/api/vazhipadu/preview", new { schedule = everyFriday, rate = 30m, qty = 1m }).Ok<JsonElement>();
        preview.GetProperty("count").GetInt32().Should().Be(4);
        preview.GetProperty("amount").GetDecimal().Should().Be(120);
        var karthika = new { kind = "StarDay", start, count = 1, star = 2 };

        var booking = await c.Post("/api/vazhipadu/bookings", new
        {
            contactName = "Raman Nair", phone = "9847000001", paymentMode = "Cash", saveDevotee = true,
            lines = new object[]
            {
                new { productId = archanaId, devoteeName = "Raman Nair", star = 21, qty = 1m, schedule = everyFriday },
                new { productId = payasam, devoteeName = "Lakshmi", star = 4, qty = 1m, schedule = karthika },
            },
        }).Ok<JsonElement>();
        long bookingId = Id(booking);
        booking.GetProperty("total").GetDecimal().Should().Be(4 * 30 + 150);
        var lines = booking.GetProperty("lines").EnumerateArray().ToList();
        lines[0].GetProperty("occurrences").GetInt32().Should().Be(4);
        lines[0].GetProperty("dates").EnumerateArray().Select(d => DateOnly.Parse(d.GetString()!).DayOfWeek).Should().OnlyContain(d => d == DayOfWeek.Friday);
        var karthikaDay = DateOnly.Parse(lines[1].GetProperty("dates")[0].GetString()!);
        (await c.GetAsync($"/api/calendar/day/{karthikaDay:yyyy-MM-dd}").Ok<JsonElement>()).GetProperty("starName").GetString().Should().Be("Karthika");
        // The devotee was remembered with the booking.
        var devotees = await c.GetAsync("/api/temple/devotees?search=Raman").Ok<JsonElement>();
        devotees.GetProperty("total").GetInt32().Should().Be(1);

        // A bad schedule is refused before anything is saved.
        (await c.Post("/api/vazhipadu/bookings", new
        {
            contactName = "X", paymentMode = "Cash",
            lines = new object[] { new { productId = archanaId, devoteeName = "X", qty = 1m, schedule = new { kind = "Daily", start } } },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // --- Pooja list on the first Friday ---
        var friday = DateOnly.Parse(lines[0].GetProperty("dates")[0].GetString()!);
        var list = await c.GetAsync($"/api/vazhipadu/pooja-list/{friday:yyyy-MM-dd}").Ok<JsonElement>();
        var row = list.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("bookingId").GetInt64() == bookingId);
        row.GetProperty("devoteeName").GetString().Should().Be("Raman Nair");
        row.GetProperty("starName").GetString().Should().Be("Thiruvonam");
        // Future days can't be marked as done.
        (await c.Post("/api/vazhipadu/pooja-list/mark", new { occurrenceIds = new[] { row.GetProperty("occurrenceId").GetInt64() }, done = true }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var pdf = await c.GetAsync($"/api/vazhipadu/pooja-list/{friday:yyyy-MM-dd}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        foreach (var paper in new[] { "", "?paper=Thermal58", "?paper=A5", "?paper=A4", "?slips=false", "?paper=A5&slips=true" })
            (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/receipt{paper}")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Summary page plus one cut slip per offering (on by default), or the summary alone.
        PdfPages(await (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/receipt")).Content.ReadAsByteArrayAsync()).Should().Be(1 + lines.Count);
        PdfPages(await (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/receipt?slips=false")).Content.ReadAsByteArrayAsync()).Should().Be(1);
        // Each slip alone, so the counter can print it as its own (separately cut) job.
        PdfPages(await (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/slips/1")).Content.ReadAsByteArrayAsync()).Should().Be(1);
        (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/slips/{lines.Count + 1}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // --- Direct printing to the receipt printer: one RAW ESC/POS job, a full cut after the receipt and each slip ---
        (await c.PostAsync($"/api/vazhipadu/bookings/{bookingId}/print", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "no receipt printer set yet");
        await c.Put("/api/company/settings", new Dictionary<string, string> { ["ReceiptPrinter"] = "Counter Printer" }).Ok();
        (await c.PostAsync($"/api/vazhipadu/bookings/{bookingId}/print", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var job = fx.Printer.Jobs.Last();
        job.Printer.Should().Be("Counter Printer");
        CountCuts(job.Data).Should().Be(1 + lines.Count);
        (await c.PostAsync($"/api/vazhipadu/bookings/{bookingId}/print?slips=false", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        CountCuts(fx.Printer.Jobs.Last().Data).Should().Be(1);
        // Duplicate receipt copies: each its own cut piece, after the receipt and before the slips.
        (await c.PostAsync($"/api/vazhipadu/bookings/{bookingId}/print?duplicates=2", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        CountCuts(fx.Printer.Jobs.Last().Data).Should().Be(1 + 2 + lines.Count);
        await c.Put("/api/company/settings", new Dictionary<string, string> { ["VazhipaduDuplicate"] = "true", ["VazhipaduDuplicateCopies"] = "3" }).Ok();
        (await c.PostAsync($"/api/vazhipadu/bookings/{bookingId}/print?slips=false", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        CountCuts(fx.Printer.Jobs.Last().Data).Should().Be(1 + 3, "the duplicate setting applies when the print does not say");
        PdfPages(await (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}/receipt?slips=false&duplicates=1")).Content.ReadAsByteArrayAsync()).Should().Be(2);
        await c.Put("/api/company/settings", new Dictionary<string, string> { ["VazhipaduDuplicate"] = "false" }).Ok();
        await c.Put("/api/company/settings", new Dictionary<string, string> { ["ReceiptPrinter"] = "" }).Ok();

        // --- Stop the Friday archana after the second week ---
        long fridayLine = Id(lines[0]);
        var third = DateOnly.Parse(lines[0].GetProperty("dates")[2].GetString()!);
        (await c.Post($"/api/vazhipadu/lines/{fridayLine}/stop", new { from = third }).Ok<JsonElement>()).GetProperty("stopped").GetInt32().Should().Be(2);

        // --- Donation ---
        long annadanam = Id(purposes.EnumerateArray().First(p => p.GetProperty("name").GetString() == "Annadanam"));
        var donation = await c.Post("/api/donations", new { donorName = "Suresh", purposeLedgerId = annadanam, amount = 5001m, mode = "Cash", pan = "ABCDE1234F" }).Ok<JsonElement>();
        donation.GetProperty("purpose").GetString().Should().Be("Annadanam");
        (await c.Post("/api/donations", new { donorName = "Bad PAN", purposeLedgerId = annadanam, amount = 10m, mode = "Cash", pan = "123" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreach (var paper in new[] { "", "?paper=Thermal80", "?paper=Thermal58", "?paper=A4" })
            (await c.GetAsync($"/api/donations/{Id(donation)}/receipt{paper}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TrialDifference(c)).Should().Be(0);

        // --- Cancelling a booking needs the password again and cancels its bill ---
        var step = (await c.Post("/api/auth/step-up", new { password = AdminPassword }).Ok<JsonElement>()).GetProperty("token").GetString()!;
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/vazhipadu/bookings/{bookingId}/cancel")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { reason = "Devotee request" }, options: ApiFixture.Json),
        };
        cancel.Headers.Add("X-Step-Up-Token", step);
        (await c.SendAsync(cancel)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetAsync($"/api/vazhipadu/bookings/{bookingId}").Ok<JsonElement>()).GetProperty("status").GetString().Should().Be("Cancelled");
        (await c.GetAsync($"/api/vazhipadu/pooja-list/{friday:yyyy-MM-dd}").Ok<JsonElement>()).GetProperty("rows").EnumerateArray()
            .Should().NotContain(r => r.GetProperty("bookingId").GetInt64() == bookingId);
        (await TrialDifference(c)).Should().Be(0);
    }
}
