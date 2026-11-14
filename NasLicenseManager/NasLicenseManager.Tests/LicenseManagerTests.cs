using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NasLicenseManager.Data;
using NasLicenseManager.Services;
using NasLicenseManager.Licensing;

namespace NasLicenseManager.Tests;

/// <summary>Runs the License Manager against a throwaway SQL Server database and a throwaway signing key.</summary>
public sealed class ManagerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly string Server = Environment.GetEnvironmentVariable("NASPOS_TEST_SQL") ?? @".\MSSQLSERVER2025";
    public string DbName { get; } = $"NasLic_Test_{Guid.NewGuid():N}"[..28];
    public string KeyFolder { get; } = Path.Combine(Path.GetTempPath(), "naslic-" + Guid.NewGuid().ToString("N"));
    public ECDsa PublicKey { get; }

    public ManagerFactory()
    {
        using var signer = new SigningKeyStore(KeyFolder).Create();
        PublicKey = ECDsa.Create();
        PublicKey.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Server={Server};Database={DbName};Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=90");
        builder.UseSetting("Signing:Folder", KeyFolder);
    }

    public Task InitializeAsync() { _ = Server; return Task.CompletedTask; }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var cn = new SqlConnection($"Server={Server};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=90");
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandTimeout = 300; // dropping can be slow on a busy test server
        cmd.CommandText = $"IF DB_ID('{DbName}') IS NOT NULL BEGIN ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DbName}]; END";
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(KeyFolder, true); } catch { /* best effort */ }
    }
}

public class LicenseManagerTests(ManagerFactory app) : IClassFixture<ManagerFactory>
{
    private static async Task<HttpResponseMessage> PostForm(HttpClient c, string page, Dictionary<string, string> fields, string? formUrl = null)
    {
        var html = await (await c.GetAsync(formUrl ?? page)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        token.Should().NotBeEmpty($"{formUrl ?? page} should render a form");
        fields["__RequestVerificationToken"] = token;
        return await c.PostAsync(page, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Setup_customer_and_a_one_year_ten_user_key_end_to_end()
    {
        var c = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        // First run: everything goes to setup until an administrator exists.
        (await c.GetAsync("/Customers")).Headers.Location!.ToString().Should().Be("/Setup");
        var setup = await PostForm(c, "/Setup", new() { ["UserName"] = "Admin", ["DisplayName"] = "Owner", ["Password"] = "Owner@2026", ["Confirm"] = "Owner@2026" });
        setup.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Customer
        var created = await PostForm(c, "/Customers/Edit", new()
        {
            ["CompanyName"] = "Sree Bhagavathy Stores", ["Gstin"] = "32abcde1234f1z5", ["ContactPerson"] = "Rajesh", ["Phone"] = "9847000001", ["City"] = "Kochi",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var details = created.Headers.Location!.ToString();
        var id = int.Parse(Regex.Match(details, @"/Customers/Details/(\d+)").Groups[1].Value);

        // Issue: 1 year, 10 users, starting 05-10-2026
        var issued = await PostForm(c, $"/Customers/Details/{id}?handler=Issue", new()
        {
            ["Years"] = "1", ["MonthsPart"] = "0", ["MaxUsers"] = "10", ["StartsOn"] = "2026-10-05", ["Notes"] = "Invoice 42",
        }, $"/Customers/Details/{id}");
        issued.StatusCode.Should().Be(HttpStatusCode.Redirect, await issued.Content.ReadAsStringAsync());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDb>();
        var row = await db.IssuedLicenses.SingleAsync();
        row.MaxUsers.Should().Be(10);
        row.ExpiresOn.Should().Be(new DateOnly(2027, 10, 4));
        row.Gstin.Should().Be("32ABCDE1234F1Z5");
        row.IssuedBy.Should().Be("Owner");

        // The key itself says the same thing, and NAS POS Pro would accept it for this company.
        var info = LicenseKey.Verify(row.Key, "SREE BHAGAVATHY STORES", "32ABCDE1234F1Z5", [app.PublicKey]);
        info.MaxUsers.Should().Be(10);
        info.Months.Should().Be(12);
        info.ExpiresOn.Should().Be(new DateOnly(2027, 10, 4));
        info.KeyId.Should().Be(row.KeyId);

        // Pages show it
        var page = await (await c.GetAsync(issued.Headers.Location!.ToString())).Content.ReadAsStringAsync();
        page.Should().Contain(row.Key).And.Contain("10 users").And.Contain("04-10-2027");
        (await (await c.GetAsync("/Licenses")).Content.ReadAsStringAsync()).Should().Contain(row.KeyId);
        var csv = await c.GetAsync("/Licenses?handler=Csv");
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (await csv.Content.ReadAsStringAsync()).Should().Contain(row.KeyId);

        // Bad input is refused, nothing is issued.
        var bad = await PostForm(c, $"/Customers/Details/{id}?handler=Issue", new() { ["Years"] = "1", ["MonthsPart"] = "0", ["MaxUsers"] = "0", ["StartsOn"] = "2026-10-05" }, $"/Customers/Details/{id}");
        bad.StatusCode.Should().Be(HttpStatusCode.OK);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("Users must be between 1");
        (await db.IssuedLicenses.CountAsync()).Should().Be(1);

        // A 1-month trial (0 years 1 month), 2 users: valid through the day before the same date next month.
        var trial = await PostForm(c, $"/Customers/Details/{id}?handler=Issue", new()
        {
            ["Years"] = "0", ["MonthsPart"] = "1", ["MaxUsers"] = "2", ["StartsOn"] = "2026-10-05", ["Notes"] = "Trial",
        }, $"/Customers/Details/{id}");
        trial.StatusCode.Should().Be(HttpStatusCode.Redirect, await trial.Content.ReadAsStringAsync());
        var trialRow = await db.IssuedLicenses.AsNoTracking().SingleAsync(l => l.Notes == "Trial");
        trialRow.Months.Should().Be(1);
        trialRow.ExpiresOn.Should().Be(new DateOnly(2026, 11, 4));
        LicenseKey.Verify(trialRow.Key, "Sree Bhagavathy Stores", "32ABCDE1234F1Z5", [app.PublicKey]).Months.Should().Be(1);
        (await (await c.GetAsync(trial.Headers.Location!.ToString())).Content.ReadAsStringAsync()).Should().Contain("1 month");

        // A temple customer gets a Kshethra Mithra key: it does not open NAS POS Pro, and the shop key does not open the temple app.
        var temple = await PostForm(c, "/Customers/Edit", new() { ["Product"] = "KSHETHRA", ["CompanyName"] = "Sree Krishna Temple" });
        var templeId = int.Parse(Regex.Match(temple.Headers.Location!.ToString(), @"/Customers/Details/(\d+)").Groups[1].Value);
        (await PostForm(c, $"/Customers/Details/{templeId}?handler=Issue", new() { ["Years"] = "1", ["MonthsPart"] = "0", ["MaxUsers"] = "5", ["StartsOn"] = "2026-10-05" },
            $"/Customers/Details/{templeId}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        var templeKey = await db.IssuedLicenses.AsNoTracking().SingleAsync(l => l.CustomerId == templeId);
        templeKey.Product.Should().Be("KSHETHRA");
        LicenseKey.Verify(templeKey.Key, "Sree Krishna Temple", null, [app.PublicKey], LicenseKey.Products.Kshethra).Months.Should().Be(12);
        ((Action)(() => LicenseKey.Verify(templeKey.Key, "Sree Krishna Temple", null, [app.PublicKey]))).Should().Throw<LicenseKeyException>();
        ((Action)(() => LicenseKey.Verify(row.Key, "Sree Bhagavathy Stores", "32ABCDE1234F1Z5", [app.PublicKey], LicenseKey.Products.Kshethra))).Should().Throw<LicenseKeyException>();
        LicenseIssuer.CustomerMessage(templeKey).Should().Contain("Kshethra Mithra license");

        // Zero length is refused.
        var zero = await PostForm(c, $"/Customers/Details/{id}?handler=Issue", new() { ["Years"] = "0", ["MonthsPart"] = "0", ["MaxUsers"] = "2", ["StartsOn"] = "2026-10-05" }, $"/Customers/Details/{id}");
        (await zero.Content.ReadAsStringAsync()).Should().Contain("at least 1 month");

        // Signed out: no access.
        var anon = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await anon.GetAsync("/Customers")).Headers.Location!.ToString().Should().StartWith("http://localhost/Login");
    }

    [Fact]
    public void Status_and_renewal_follow_the_longest_key()
    {
        var today = new DateOnly(2027, 9, 20);
        var oneYear = new IssuedLicense { Months = 12, StartsOn = new(2026, 10, 5), ExpiresOn = new(2027, 10, 4), IssuedAt = DateTimeOffset.Now.AddYears(-1) };
        var state = CustomerLicenseState.Of([oneYear], today);
        state.State.Should().Be("Ending soon");
        LicenseIssuer.RenewalStart(state, today).Should().Be(new DateOnly(2027, 10, 5), "a renewal starts the day after the current key ends");
        CustomerLicenseState.Of([oneYear], new DateOnly(2027, 10, 5)).State.Should().Be("Expired");
        LicenseIssuer.RenewalStart(CustomerLicenseState.Of([oneYear], new DateOnly(2027, 11, 1)), new DateOnly(2027, 11, 1)).Should().Be(new DateOnly(2027, 11, 1));
        var lifetime = new IssuedLicense { Months = null, IssuedAt = DateTimeOffset.Now };
        CustomerLicenseState.Of([oneYear, lifetime], today).State.Should().Be("Lifetime");
        CustomerLicenseState.Of([], today).State.Should().Be("No key yet");
    }
}
