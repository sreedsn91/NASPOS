using System.Net;
using System.Text.Json;
using NasChatbot.Admin;
using NasChatbot.Knowledge;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Chatbot").Get<ChatbotOptions>() ?? new ChatbotOptions();
var dataDir = Path.GetFullPath(Path.IsPathRooted(options.DataDir)
    ? options.DataDir
    : Path.Combine(builder.Environment.ContentRootPath, options.DataDir));
var seedPath = Path.Combine(AppContext.BaseDirectory, "seed", "knowledge.seed.json");

builder.Services.AddSingleton(new KnowledgeStore(dataDir, seedPath));
builder.Services.AddSingleton(new AdminAuth(dataDir));
builder.Services.AddSingleton<ChatService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------- chat (anyone who can reach the bot) ----------------

var chat = app.MapGroup("/api/chat");

chat.MapGet("/start", (ChatService bot, KnowledgeStore store) => new
{
    greeting = "Hello! I'm NAS Chatbot. Ask me anything about using NAS POS Pro.",
    suggestions = bot.Popular(),
    count = store.Engine.Count,
});

chat.MapPost("/", (AskRequest req, ChatService bot) => bot.Ask(req.Message ?? ""));

chat.MapGet("/answer/{id}", (string id, ChatService bot) =>
    bot.AnswerById(id) is { } reply ? Results.Ok(reply) : Results.NotFound());

chat.MapPost("/feedback", (FeedbackRequest req, KnowledgeStore store) =>
{
    if (!req.Helpful && !string.IsNullOrWhiteSpace(req.Question)) store.RecordUnanswered(req.Question, "not-helpful");
    return Results.NoContent();
});

// ---------------- admin: sign-in ----------------

var auth = app.MapGroup("/api/admin");

auth.MapGet("/status", (AdminAuth admin, HttpContext ctx) => new { configured = admin.IsConfigured, local = IsLocal(ctx) });

auth.MapPost("/setup", (PasswordRequest req, AdminAuth admin, HttpContext ctx) =>
{
    if (!IsLocal(ctx)) return Problem("The admin password can only be set from the computer running NAS Chatbot.", 403);
    if (AdminAuth.ValidateNewPassword(req.Password) is { } problem) return Problem(problem);
    return admin.Setup(req.Password!) is { } token ? Results.Ok(new { token }) : Problem("The admin password is already set. Sign in instead.", 409);
});

auth.MapPost("/login", (PasswordRequest req, AdminAuth admin) =>
{
    var (token, error) = admin.Login(req.Password);
    return token is not null ? Results.Ok(new { token }) : Problem(error!, 401);
});

// ---------------- admin: manage questions (signed-in only) ----------------

var manage = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
{
    var admin = ctx.HttpContext.RequestServices.GetRequiredService<AdminAuth>();
    return admin.IsValid(BearerToken(ctx.HttpContext)) ? await next(ctx) : Problem("Please sign in again.", 401);
});

manage.MapPost("/logout", (AdminAuth admin, HttpContext ctx) => { admin.Logout(BearerToken(ctx)); return Results.NoContent(); });

manage.MapPost("/password", (ChangePasswordRequest req, AdminAuth admin) =>
    admin.ChangePassword(req.Current, req.Next) is { } error ? Problem(error) : Results.NoContent());

manage.MapGet("/entries", (KnowledgeStore store) => new { entries = store.All(), categories = store.Categories() });

manage.MapPost("/entries", (QaInput input, KnowledgeStore store) =>
    Guarded(() => Results.Ok(store.Add(input))));

manage.MapPut("/entries/{id}", (string id, QaInput input, KnowledgeStore store) =>
    Guarded(() => store.Update(id, input) is { } e ? Results.Ok(e) : Results.NotFound()));

manage.MapDelete("/entries/{id}", (string id, KnowledgeStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound());

manage.MapGet("/unanswered", (KnowledgeStore store) => store.Unanswered());

manage.MapDelete("/unanswered/{id}", (string id, KnowledgeStore store) =>
    store.DismissUnanswered(id) ? Results.NoContent() : Results.NotFound());

manage.MapGet("/test", (string? q, KnowledgeStore store) => new
{
    understood = store.Engine.Understand(q ?? ""),
    answerAt = AnswerEngine.AnswerThreshold,
    maybeAt = AnswerEngine.MaybeThreshold,
    matches = store.Engine.Search(q ?? "", 5).Select(m => new { m.Entry.Id, m.Entry.Question, m.Entry.Category, m.Confidence }),
});

manage.MapGet("/export", (KnowledgeStore store) =>
{
    var entries = store.All().Select(e => new QaInput(e.Question, e.Variants, e.Answer, e.Category, e.Tags));
    var json = JsonSerializer.SerializeToUtf8Bytes(new { entries }, KnowledgeStore.Json);
    return Results.File(json, "application/json", $"nas-chatbot-questions-{DateTime.Now:yyyyMMdd-HHmm}.json");
});

manage.MapPost("/import", (ImportRequest req, KnowledgeStore store) =>
    Guarded(() => Results.Ok(new { imported = store.Import(req.Entries ?? [], req.Replace) })));

app.Logger.LogInformation("NAS Chatbot data folder: {DataDir}", dataDir);
app.Run();

static IResult Guarded(Func<IResult> action)
{
    try { return action(); }
    catch (KnowledgeValidationException ex) { return Problem(ex.Message); }
}

static IResult Problem(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);

static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

static string? BearerToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
}

internal sealed record AskRequest(string? Message);
internal sealed record FeedbackRequest(string? Question, string? EntryId, bool Helpful);
internal sealed record PasswordRequest(string? Password);
internal sealed record ChangePasswordRequest(string? Current, string? Next);
internal sealed record ImportRequest(List<QaInput>? Entries, bool Replace);

public partial class Program;
