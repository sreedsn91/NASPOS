using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NasPos.Domain.Validation;

namespace NasPos.Tests.Integration;

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;

/// <summary>
/// One ordered business scenario on a real SQL Server database: every step asserts the books balance and stock reconciles.
/// </summary>
[Collection("api")]
public class EndToEndTests(ApiFixture fx)
{
    private const string AdminPassword = "Admin@12345";
    private static string Gst(string first14) => first14 + Gstin.CheckChar(first14);

    private static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private async Task<string> StepUp(HttpClient c, string password = AdminPassword) =>
        (await c.Post("/api/auth/step-up", new { password }).Ok<JsonElement>()).GetProperty("token").GetString()!;

    private static async Task<decimal> TrialDifference(HttpClient c)
    {
        var tb = await c.Post("/api/reports/trial-balance", new { }).Ok<JsonElement>();
        var summary = tb.GetProperty("summary").EnumerateArray().First(s => s.GetProperty("key").GetString() == "Difference");
        return decimal.Parse(summary.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> StockOf(HttpClient c, long productId)
    {
        var r = await c.Post("/api/reports/stock-summary", new { productId, detailed = true }).Ok<JsonElement>();
        return r.GetProperty("rows").EnumerateArray().Sum(x => x.GetProperty("qty").GetDecimal());
    }

    private static object Sale(long product, long? batch, decimal qty, decimal rate, long salesperson, string mode = "Cash", long? party = null,
        decimal card = 0, decimal cash = 0, decimal credit = 0, string type = "Sales") => new
    {
        voucherType = type, date = DateTimeOffset.Now, paymentMode = mode, partyLedgerId = party, salespersonId = salesperson,
        cardAmount = card, cashAmount = cash, creditAmount = credit, cardLast4 = card > 0 ? "4242" : null,
        lines = new[] { new { productId = product, batchId = batch, qty, rate, taxInclusive = false } },
    };

    [Fact]
    public async Task Full_business_flow_keeps_books_balanced_and_stock_reconciled()
    {
        var c = fx.NewClient();

        // --- First-run setup (no default password ships) ---
        var status = await c.GetFromJsonAsync<JsonElement>("/api/auth/setup-status", ApiFixture.Json);
        status.GetProperty("setupRequired").GetBoolean().Should().BeTrue();
        var setup = await c.Post("/api/auth/setup", new
        {
            companyName = "Test Traders", stateCode = "32", gstin = Gst("32ABCDE1234F1Z"), address = "MG Road", city = "Kochi",
            adminUserName = "admin", adminPassword = AdminPassword, adminDisplayName = "Admin",
        }).Ok<JsonElement>();
        c.Bearer(setup.GetProperty("accessToken").GetString()!);
        (await c.GetAsync("/api/auth/setup-status").Ok<JsonElement>()).GetProperty("setupRequired").GetBoolean().Should().BeFalse();
        var me = await c.GetAsync("/api/auth/me").Ok<JsonElement>();
        long companyId = me.GetProperty("company").GetProperty("id").GetInt64();

        await c.Put("/api/company/settings", new Dictionary<string, string>
        {
            ["EnableLoyalty"] = "true", ["NegativeStockMode"] = "Block", ["EnableBillByBill"] = "true", ["RequireSalesperson"] = "true",
        }).Ok();

        // --- Masters ---
        long sp = (await c.Post("/api/employees", new { name = "Ravi", commissionPercent = 1, isActive = true, isSalesperson = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        long scheme = (await c.Post("/api/loyalty-schemes", new { name = "Gold", saleAmount = 100, points = 1, targetPoints = 1, pointValue = 1, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        var groups = await c.GetAsync("/api/account-groups").Ok<JsonElement>();
        long debtors = groups.EnumerateArray().First(g => g.GetProperty("systemCode").ValueKind == JsonValueKind.String && g.GetProperty("systemCode").GetString() == "SundryDebtors").GetProperty("id").GetInt64();
        long creditors = groups.EnumerateArray().First(g => g.GetProperty("systemCode").ValueKind == JsonValueKind.String && g.GetProperty("systemCode").GetString() == "SundryCreditors").GetProperty("id").GetInt64();
        long customer = (await c.Post("/api/ledgers", new { name = "Anil Kumar", accountGroupId = debtors, partyType = "Customer", mobile = "9800000001", stateCode = "32", loyaltySchemeId = scheme, creditLimit = 100000, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        long supplier = (await c.Post("/api/ledgers", new { name = "Metro Suppliers", accountGroupId = creditors, partyType = "Supplier", gstin = Gst("29ABCDE1234F1Z"), stateCode = "29", isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        long soap = (await c.Post("/api/products", new { name = "Soap 100g", hsnCode = "3401", gstRate = 18, purchaseRate = 60, saleRate = 100, mrp = 120, mainUnit = "NOS", conversionFactor = 1, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        long service = (await c.Post("/api/products", new { name = "Gift wrap", hsnCode = "9985", itemClass = "Service", gstRate = 18, saleRate = 50, mainUnit = "NOS", conversionFactor = 1, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64();

        // Invalid GSTIN is rejected server-side.
        (await c.Post("/api/ledgers", new { name = "Bad", accountGroupId = debtors, gstin = "32ABCDE1234F1ZZ", isActive = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Per-user theme: saved on the account and returned with the session.
        (await c.GetAsync("/api/auth/me").Ok<JsonElement>()).GetProperty("user").GetProperty("theme").GetString().Should().Be("light");
        (await c.PutAsJsonAsync("/api/auth/me/theme", new { theme = "dark" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetAsync("/api/auth/me").Ok<JsonElement>()).GetProperty("user").GetProperty("theme").GetString().Should().Be("dark");
        (await c.PutAsJsonAsync("/api/auth/me/theme", new { theme = "sunset" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetAsync("/api/auth/me").Ok<JsonElement>()).GetProperty("user").GetProperty("theme").GetString().Should().Be("sunset");
        (await c.PutAsJsonAsync("/api/auth/me/theme", new { theme = "neon" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Duplicate names: rejected for individuals, allowed for corporate parties (branches).
        (await c.Post("/api/ledgers", new { name = "Anil Kumar", accountGroupId = debtors, partyType = "Customer", isActive = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.Post("/api/ledgers", new { name = "Reliance Retail", accountGroupId = creditors, partyType = "Supplier", isCorporate = true, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64().Should().BePositive();
        (await c.Post("/api/ledgers", new { name = "Reliance Retail", accountGroupId = creditors, partyType = "Supplier", isCorporate = true, isActive = true }).Ok<JsonElement>()).GetProperty("id").GetInt64().Should().BePositive();
        // Individual names stay unique across customers and suppliers.
        (await c.Post("/api/ledgers", new { name = "Anil Kumar", accountGroupId = creditors, partyType = "Supplier", isActive = true })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Search input is treated as data, never as SQL.
        (await c.GetAsync("/api/products?search=%27%3B%20DROP%20TABLE%20Products%3B--")).StatusCode.Should().Be(HttpStatusCode.OK);

        // --- Purchase (inter-state: IGST) ---
        var purchase = await c.Post("/api/documents", new
        {
            voucherType = "Purchase", date = DateTimeOffset.Now, paymentMode = "Credit", partyLedgerId = supplier, referenceNo = "INV-77",
            lines = new[] { new { productId = soap, qty = 10m, rate = 60m, mrp = 120m, saleRate = 100m } },
        }).Ok<JsonElement>();
        purchase.GetProperty("taxType").GetString().Should().Be("InterState");
        Dec(purchase, "igstAmount").Should().Be(108);
        Dec(purchase, "billAmount").Should().Be(708);
        long batch = purchase.GetProperty("lines")[0].GetProperty("batchId").GetInt64();
        (await StockOf(c, soap)).Should().Be(10);
        (await TrialDifference(c)).Should().Be(0);

        // --- Sales: cash, credit (loyalty), mixed ---
        var cash = await c.Post("/api/documents", Sale(soap, batch, 2, 100, sp)).Ok<JsonElement>();
        Dec(cash, "cgstAmount").Should().Be(18);
        Dec(cash, "billAmount").Should().Be(236);
        var credit = await c.Post("/api/documents", Sale(soap, batch, 3, 100, sp, "Credit", customer)).Ok<JsonElement>();
        Dec(credit, "billAmount").Should().Be(354);
        Dec(credit, "pointsEarned").Should().Be(3.54m);
        var mixed = await c.Post("/api/documents", Sale(soap, batch, 1, 100, sp, "Mixed", null, card: 100, cash: 18)).Ok<JsonElement>();
        mixed.GetProperty("cardLast4").GetString().Should().Be("4242");

        // Full card numbers are refused (never stored).
        var pan = await c.Post("/api/documents", new { voucherType = "Sales", date = DateTimeOffset.Now, paymentMode = "Mixed", salespersonId = sp, cardAmount = 118, cardLast4 = "4111111111111111", lines = new[] { new { productId = soap, batchId = batch, qty = 1m, rate = 100m } } });
        pan.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Salesperson is required; credit needs a real customer; negative stock is blocked.
        (await c.Post("/api/documents", new { voucherType = "Sales", date = DateTimeOffset.Now, lines = new[] { new { productId = soap, batchId = batch, qty = 1m, rate = 100m } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.Post("/api/documents", Sale(soap, batch, 1, 100, sp, "Credit"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var neg = await c.Post("/api/documents", Sale(soap, batch, 100, 100, sp));
        neg.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await neg.Content.ReadAsStringAsync()).Should().Contain("negative_stock");
        (await StockOf(c, soap)).Should().Be(4);

        // --- Sales return and bill-wise receipt ---
        await c.Post("/api/documents", Sale(soap, batch, 1, 100, sp, "Credit", customer, type: "SalesReturn")).Ok<JsonElement>();
        (await StockOf(c, soap)).Should().Be(5);
        var bills = await c.GetAsync($"/api/vouchers/open-bills/{customer}").Ok<JsonElement>();
        long creditBill = bills.EnumerateArray().First(b => b.GetProperty("documentNo").GetString() == credit.GetProperty("documentNo").GetString()).GetProperty("id").GetInt64();
        var ledgers = await c.GetAsync("/api/ledgers?search=Cash").Ok<JsonElement>();
        long cashLedger = ledgers.GetProperty("items").EnumerateArray().First(l => l.GetProperty("code").GetString() == "CASH").GetProperty("id").GetInt64();
        var receipt = await c.Post("/api/vouchers", new
        {
            voucherType = "Receipt", date = DateTimeOffset.Now, headerLedgerId = cashLedger,
            lines = new[] { new { ledgerId = customer, amount = 200m, allocations = new[] { new { billId = creditBill, amount = 200m } } } },
        }).Ok<JsonElement>();
        receipt.GetProperty("lines")[0].GetProperty("allocations")[0].GetProperty("amount").GetDecimal().Should().Be(200);
        var cust = await c.GetAsync($"/api/ledgers/{customer}").Ok<JsonElement>();
        Dec(cust, "balance").Should().Be(354 - 118 - 200);
        (await TrialDifference(c)).Should().Be(0);

        // --- Cancel: settled bill is protected; cancel reverses stock and loyalty by the real document id ---
        var token = await StepUp(c);
        (await c.Post($"/api/documents/{credit.GetProperty("id").GetInt64()}/cancel", new { reason = "test" })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "step-up header missing");
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/documents/{credit.GetProperty("id").GetInt64()}/cancel") { Content = JsonContent.Create(new { reason = "x" }) };
        req.Headers.Add("X-Step-Up-Token", token);
        (await c.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a bill with settlements cannot be cancelled");

        var extra = await c.Post("/api/documents", Sale(soap, batch, 1, 100, sp, "Credit", customer)).Ok<JsonElement>();
        Dec((await c.GetAsync($"/api/ledgers/{customer}").Ok<JsonElement>()), "loyaltyPoints").Should().Be(3.54m + 1.18m);
        req = new HttpRequestMessage(HttpMethod.Post, $"/api/documents/{extra.GetProperty("id").GetInt64()}/cancel") { Content = JsonContent.Create(new { reason = "wrong item" }) };
        req.Headers.Add("X-Step-Up-Token", token);
        await c.SendAsync(req).Ok();
        Dec((await c.GetAsync($"/api/ledgers/{customer}").Ok<JsonElement>()), "loyaltyPoints").Should().Be(3.54m);
        (await StockOf(c, soap)).Should().Be(5);
        var voidReport = await c.Post("/api/reports/void-report", new { }).Ok<JsonElement>();
        voidReport.GetProperty("rows").GetArrayLength().Should().BeGreaterThan(0);

        // --- Edit = reverse + repost under the same number ---
        long cashId = cash.GetProperty("id").GetInt64();
        var editBody = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(Sale(soap, batch, 1, 100, sp), ApiFixture.Json), ApiFixture.Json)!;
        editBody["rowVersion"] = cash.GetProperty("rowVersion").GetString();
        var edited = await c.Put($"/api/documents/{cashId}", editBody).Ok<JsonElement>();
        edited.GetProperty("documentNo").GetString().Should().Be(cash.GetProperty("documentNo").GetString());
        edited.GetProperty("version").GetInt32().Should().Be(2);
        (await StockOf(c, soap)).Should().Be(6);
        // A stale row version is rejected (optimistic concurrency).
        (await c.Put($"/api/documents/{cashId}", editBody)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TrialDifference(c)).Should().Be(0);

        // --- Concurrent counters get distinct numbers ---
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
        {
            var cl = fx.NewClient($"TEST-{i}");
            cl.Bearer(setup.GetProperty("accessToken").GetString()!);
            return cl.Post("/api/documents", Sale(service, null, 1, 50, sp)).Ok<JsonElement>();
        }));
        parallel.Select(p => p.GetProperty("documentNo").GetString()).Distinct().Should().HaveCount(6);
        (await TrialDifference(c)).Should().Be(0);

        // --- Every report runs (parameterised queries) and exports ---
        var catalog = await c.GetAsync("/api/reports").Ok<JsonElement>();
        catalog.GetArrayLength().Should().BeGreaterThan(60);
        foreach (var def in catalog.EnumerateArray())
        {
            string key = def.GetProperty("key").GetString()!;
            var resp = await c.Post($"/api/reports/{key}", new { ledgerId = customer, productId = soap });
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"report {key}: {await resp.Content.ReadAsStringAsync()}");
        }
        (await c.Post("/api/reports/sales-daybook/export?format=xlsx", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.Post("/api/reports/profit-loss/export?format=pdf", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.Post("/api/reports/gstr1/workbook", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        var b2cs = await c.Post("/api/reports/gstr1-b2cs", new { }).Ok<JsonElement>();
        b2cs.GetProperty("totals").GetProperty("taxable").GetDecimal().Should().BeGreaterThan(0);
        var bs = await c.Post("/api/reports/balance-sheet", new { }).Ok<JsonElement>();
        bs.GetProperty("summary")[0].GetProperty("value").GetString().Should().Be("0.00", "balance sheet must balance");
        (await c.GetAsync($"/api/documents/{cashId}/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"/api/documents/{cashId}/text")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"/api/vouchers/{receipt.GetProperty("id").GetInt64()}/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.Post("/api/print/labels", new { items = new[] { new { productId = soap, batchId = batch, copies = 4 } } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync("/api/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);

        // --- Permissions are enforced on the server ---
        var roleId = (await c.Post("/api/roles", new { name = "Cashier", isActive = true, permissions = new[] { new { screen = "Sales", rights = 3 } } }).Ok<JsonElement>()).GetProperty("id").GetInt64();
        await c.Post("/api/users", new { userName = "cashier1", displayName = "Cashier", isActive = true, roleId, password = "Cashier@123" }).Ok<JsonElement>();
        var cashier = fx.NewClient("TEST-C");
        var login = await cashier.Post("/api/auth/login", new { userName = "cashier1", password = "Cashier@123" }).Ok<JsonElement>();
        cashier.Bearer(login.GetProperty("accessToken").GetString()!);
        (await cashier.Post("/api/products", new { name = "X", gstRate = 5, conversionFactor = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/dashboard")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await cashier.Post("/api/documents", Sale(service, null, 1, 50, sp))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await cashier.Post("/api/auth/login", new { userName = "cashier1", password = "wrong" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // --- Backup and restore into a new company (logical, id-remapped) ---
        var backup = await c.GetByteArrayAsync("/api/company/backup");
        backup.Length.Should().BeGreaterThan(1000);
        var form = new MultipartFormDataContent { { new ByteArrayContent(backup), "file", "backup.zip" }, { new StringContent("Test Traders Copy"), "name" } };
        var restoreReq = new HttpRequestMessage(HttpMethod.Post, "/api/company/restore") { Content = form };
        restoreReq.Headers.Add("X-Step-Up-Token", await StepUp(c));
        long restoredId = (await c.SendAsync(restoreReq).Ok<JsonElement>()).GetProperty("id").GetInt64();
        var rc = fx.NewClient("TEST-R");
        var switched = await c.Post("/api/auth/switch", new { companyId = restoredId }).Ok<JsonElement>();
        rc.Bearer(switched.GetProperty("accessToken").GetString()!);
        (await TrialDifference(rc)).Should().Be(0);
        (await StockOf(rc, (await rc.GetAsync("/api/products?search=Soap").Ok<JsonElement>()).GetProperty("items")[0].GetProperty("id").GetInt64())).Should().Be(6);

        // --- Year-end: one transaction, prior year preserved and read-only ---
        var back = await c.Post("/api/auth/switch", new { companyId }).Ok<JsonElement>();
        c.Bearer(back.GetProperty("accessToken").GetString()!);
        var preview = await c.GetAsync("/api/year-end/preview").Ok<JsonElement>();
        preview.GetProperty("blockers").GetArrayLength().Should().Be(0, preview.ToString());
        var closeReq = new HttpRequestMessage(HttpMethod.Post, "/api/year-end/close");
        closeReq.Headers.Add("X-Step-Up-Token", await StepUp(c));
        var closed = await c.SendAsync(closeReq).Ok<JsonElement>();
        long newFy = closed.GetProperty("newFinancialYearId").GetInt64();
        (await c.Post("/api/documents", Sale(service, null, 1, 50, sp))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the closed year is read-only");
        (await TrialDifference(c)).Should().Be(0, "the closed year is still queryable");
        var next = await c.Post("/api/auth/switch", new { companyId, financialYearId = newFy }).Ok<JsonElement>();
        c.Bearer(next.GetProperty("accessToken").GetString()!);
        (await TrialDifference(c)).Should().Be(0);
        (await StockOf(c, soap)).Should().Be(6);
        Dec((await c.GetAsync($"/api/ledgers/{customer}").Ok<JsonElement>()), "balance").Should().Be(36);
        Dec((await c.GetAsync($"/api/ledgers/{customer}").Ok<JsonElement>()), "loyaltyPoints").Should().Be(3.54m);
    }
}
