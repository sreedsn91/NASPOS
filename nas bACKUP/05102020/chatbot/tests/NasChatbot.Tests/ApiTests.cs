using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NasChatbot.Knowledge;

namespace NasChatbot.Tests;

/// <summary>Runs the real app in memory against a throwaway data folder.</summary>
public sealed class ChatbotFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "nas-chatbot-tests-" + Guid.NewGuid().ToString("N"));
    public bool Local { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Chatbot:DataDir", DataDir);
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new RemoteAddressFilter(this)));
    }

    // The in-memory test server has no client address; pretend requests come from this PC (or from the LAN).
    private sealed class RemoteAddressFilter(ChatbotFactory owner) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress = owner.Local ? IPAddress.Loopback : IPAddress.Parse("192.168.1.50");
                return nxt();
            });
            next(app);
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
    }
}

public class ApiTests : IDisposable
{
    private readonly ChatbotFactory _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private sealed record Reply(string Kind, string Text, string? EntryId, string? Question, double Confidence);
    private sealed record TokenResponse(string Token);

    private async Task<string> SignInAsAdmin()
    {
        var res = await _client.PostAsJsonAsync("/api/admin/setup", new { password = "Admin@2026" });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    [Fact]
    public async Task Seeded_knowledge_answers_the_roles_question()
    {
        var reply = await (await _client.PostAsJsonAsync("/api/chat", new { message = "Where I can create new user roles" }))
            .Content.ReadFromJsonAsync<Reply>();

        reply!.Kind.Should().Be("answer");
        reply.Text.Should().Contain("Settings › Roles & Rights");
        File.Exists(Path.Combine(_factory.DataDir, "knowledge.json")).Should().BeTrue("the seed is copied into the data folder on first run");
    }

    [Fact]
    public async Task Unknown_question_is_logged_for_the_admin()
    {
        var reply = await (await _client.PostAsJsonAsync("/api/chat", new { message = "how do I fly an aeroplane" }))
            .Content.ReadFromJsonAsync<Reply>();
        reply!.Kind.Should().Be("unknown");

        await SignInAsAdmin();
        var log = await _client.GetFromJsonAsync<List<UnansweredQuestion>>("/api/admin/unanswered");
        log.Should().ContainSingle(u => u.Text == "how do I fly an aeroplane" && u.Reason == "no-match");
    }

    [Fact]
    public async Task Admin_endpoints_need_sign_in()
    {
        (await _client.GetAsync("/api/admin/entries")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "made-up");
        (await _client.GetAsync("/api/admin/entries")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_password_can_only_be_set_once_and_only_locally()
    {
        _factory.Local = false;
        (await _client.PostAsJsonAsync("/api/admin/setup", new { password = "Admin@2026" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.Local = true;
        (await _client.PostAsJsonAsync("/api/admin/setup", new { password = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await SignInAsAdmin();
        (await _client.PostAsJsonAsync("/api/admin/setup", new { password = "Other@2026" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _client.PostAsJsonAsync("/api/admin/login", new { password = "wrong-123" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsJsonAsync("/api/admin/login", new { password = "Admin@2026" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_adds_a_question_and_the_bot_answers_it_immediately()
    {
        await SignInAsAdmin();
        var created = await _client.PostAsJsonAsync("/api/admin/entries", new
        {
            question = "What is the helpline number?",
            variants = new[] { "support phone", "who do I call for help" },
            category = "Support",
            tags = Array.Empty<string>(),
            answer = "Call 0484 000 0000 between 9 am and 6 pm.",
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = await created.Content.ReadFromJsonAsync<QaEntry>();

        var reply = await (await _client.PostAsJsonAsync("/api/chat", new { message = "support phone number" })).Content.ReadFromJsonAsync<Reply>();
        reply!.EntryId.Should().Be(entry!.Id);
        reply.Text.Should().Contain("0484");

        (await _client.DeleteAsync("/api/admin/entries/" + entry.Id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        reply = await (await _client.PostAsJsonAsync("/api/chat", new { message = "support phone number" })).Content.ReadFromJsonAsync<Reply>();
        reply!.EntryId.Should().NotBe(entry.Id);
    }

    [Fact]
    public async Task Invalid_entries_are_rejected_with_a_message()
    {
        await SignInAsAdmin();
        var res = await _client.PostAsJsonAsync("/api/admin/entries", new { question = "", answer = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Question is required");
    }

    [Fact]
    public async Task Import_is_all_or_nothing()
    {
        await SignInAsAdmin();
        var before = (await _client.GetFromJsonAsync<EntriesResponse>("/api/admin/entries"))!.Entries.Count;

        var bad = await _client.PostAsJsonAsync("/api/admin/import", new
        {
            replace = true,
            entries = new object[] { new { question = "Good one", answer = "Fine" }, new { question = "Missing answer", answer = "" } },
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("Entry 2");
        (await _client.GetFromJsonAsync<EntriesResponse>("/api/admin/entries"))!.Entries.Should().HaveCount(before);
    }

    private sealed record EntriesResponse(List<QaEntry> Entries, List<string> Categories);
}

public class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nas-chatbot-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void Changes_survive_a_restart()
    {
        var store = new KnowledgeStore(_dir, seedPath: null);
        var added = store.Add(new QaInput("How do I open the cash drawer?", ["cash drawer"], "Press F2 in POS.", "Sales", null));
        store.Update(added.Id, new QaInput("How do I open the cash drawer?", ["cash drawer", "drawer not opening"], "It opens on Complete sale.", "Sales", null));
        store.RecordUnanswered("why is the sky blue", "no-match");
        store.RecordUnanswered("Why is the sky blue", "no-match");

        var reopened = new KnowledgeStore(_dir, seedPath: null);
        reopened.All().Should().ContainSingle().Which.Answer.Should().Be("It opens on Complete sale.");
        reopened.Unanswered().Should().ContainSingle().Which.Count.Should().Be(2);
        reopened.Engine.Search("drawer not opening").First().Entry.Id.Should().Be(added.Id);
    }
}
