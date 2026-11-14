using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Reports;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Help;

public sealed record FaqStart(string Greeting, IReadOnlyList<FaqSuggestion> Suggestions, int Count, IReadOnlyList<string> Categories);

public sealed record FaqEntryDto(long Id, string Question, List<string> Variants, string Answer, string Category, List<string> Tags,
    List<string> Screens, Right Right, string? Link, bool IsActive);

/// <summary>Any answer, built-in or company-made, as listed on the manage screen.</summary>
public sealed record FaqItemDto(string Id, long? EntryId, string Question, IReadOnlyList<string> Variants, string Answer, string Category,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Screens, string Right, string? Link, bool BuiltIn);

public sealed record FaqLogDto(long Id, string Text, string Reason, int Count, string? LastUserName, DateTimeOffset LastAskedAt);

public sealed record FaqTestMatch(string Id, string Question, string Category, double Confidence, bool BuiltIn, IReadOnlyList<string> Screens);
public sealed record FaqTestResult(IReadOnlyList<string> Understood, double AnswerAt, double MaybeAt, IReadOnlyList<FaqTestMatch> Matches);

/// <summary>
/// The FAQ chatbot: answers questions offline from the built-in answers plus the company's own, filtered by the
/// signed-in user's role, and keeps a list of unanswered questions for the administrator.
/// </summary>
public class FaqService(IAppDbContext db, ICurrentUser user, IClock clock, AuditService audit, IEnumerable<IReportProvider> reports)
{
    public const int MaxQuestion = 300, MaxAnswer = 6000, MaxVariants = 40, MaxLogRows = 500;

    private static readonly object BuiltInGate = new();
    private static IReadOnlyList<FaqItem>? _builtIn;
    // Per company: fingerprint of its own answers → engine over built-in + own answers.
    private static readonly ConcurrentDictionary<long, (string Stamp, AnswerEngine Engine)> Engines = new();

    private IReadOnlyList<FaqItem> BuiltIn()
    {
        if (_builtIn is not null) return _builtIn;
        lock (BuiltInGate)
            return _builtIn ??= FaqKnowledge.Curated.Concat(FaqKnowledge.ForReports(reports.SelectMany(p => p.Definitions))).ToList();
    }

    private bool Can(string screen, Right right) => user.HasRight(screen, right);

    public async Task<AnswerEngine> EngineAsync(CancellationToken ct)
    {
        var own = await db.FaqEntries.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.Id).ToListAsync(ct);
        var stamp = own.Count + "|" + string.Join(",", own.Select(e => $"{e.Id}:{(e.UpdatedAt ?? e.CreatedAt).UtcTicks}"));
        if (Engines.TryGetValue(user.CompanyId, out var cached) && cached.Stamp == stamp) return cached.Engine;
        var engine = new AnswerEngine(BuiltIn().Concat(own.Select(ToItem)));
        Engines[user.CompanyId] = (stamp, engine);
        return engine;
    }

    // ---------------- chatbot (every signed-in user) ----------------

    public async Task<FaqStart> StartAsync(CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        var visible = engine.Items.Count(i => FaqResponder.IsVisible(i, Can));
        return new FaqStart(
            "Hello! I'm the NAS POS Pro help bot. I work offline and answer questions about the screens your role can use.",
            FaqResponder.Starters(engine, Can), visible, FaqResponder.Categories(engine, Can));
    }

    public async Task<FaqReply> AskAsync(string message, CancellationToken ct)
    {
        var reply = FaqResponder.Ask(await EngineAsync(ct), message, Can);
        if (reply.LogReason is not null) await LogAsync(message, reply.LogReason, ct);
        return Personalise(reply);
    }

    public async Task<FaqReply> AnswerAsync(string id, CancellationToken ct) =>
        Personalise(FaqResponder.AnswerById(await EngineAsync(ct), id, Can) ?? throw new NotFoundException("That answer is not available."));

    /// <summary>Fills answers that depend on who is asking (the "what can I do with my role" answer).</summary>
    private FaqReply Personalise(FaqReply reply)
    {
        if (!reply.Text.Contains(MyAccessTag)) return reply;
        string text;
        if (user.IsAdmin) text = "You are an **administrator**, so you can use every screen and every right in this company.";
        else
        {
            var names = Screens.All.Where(s => user.HasRight(s, Right.Read)).Select(s => FaqResponder.ScreenNames.GetValueOrDefault(s, s)).ToList();
            text = names.Count == 0
                ? "Your role doesn't have rights on any screen yet. Please ask your administrator to set them in Settings › Roles & Rights."
                : "Your role can use:\n" + string.Join("\n", names.Select(n => "- " + n)) +
                  "\n\nI answer questions about these screens. For anything else, ask your administrator to give your role the right in Settings › Roles & Rights.";
        }
        return reply with { Text = reply.Text.Replace(MyAccessTag, text) };
    }

    private const string MyAccessTag = "{{MY_ACCESS}}";

    public Task FeedbackAsync(string question, bool helpful, CancellationToken ct) =>
        helpful || string.IsNullOrWhiteSpace(question) ? Task.CompletedTask : LogAsync(question, "not-helpful", ct);

    private async Task LogAsync(string text, string reason, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxQuestion) text = text[..MaxQuestion];
        var row = await db.FaqQuestionLogs.FirstOrDefaultAsync(l => l.Text == text, ct);
        if (row is null)
        {
            if (await db.FaqQuestionLogs.CountAsync(ct) >= MaxLogRows)
            {
                var oldest = await db.FaqQuestionLogs.OrderBy(l => l.LastAskedAt).FirstAsync(ct);
                db.FaqQuestionLogs.Remove(oldest);
            }
            db.FaqQuestionLogs.Add(new FaqQuestionLog { Text = text, Reason = reason, Count = 1, LastUserName = user.UserName, LastAskedAt = clock.Now });
        }
        else
        {
            row.Count++;
            row.LastAskedAt = clock.Now;
            row.LastUserName = user.UserName;
            if (reason != "no-match") row.Reason = reason;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------- manage (Faq rights) ----------------

    public async Task<List<FaqItemDto>> ListAsync(CancellationToken ct)
    {
        var own = await db.FaqEntries.AsNoTracking().OrderBy(e => e.Category).ThenBy(e => e.Question).ToListAsync(ct);
        return own.Select(e => ToDto(ToItem(e), e.Id))
            .Concat(BuiltIn().Select(i => ToDto(i, null)))
            .ToList();
    }

    public async Task<FaqEntryDto> GetAsync(long id, CancellationToken ct)
    {
        var e = await db.FaqEntries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Question not found.");
        return new FaqEntryDto(e.Id, e.Question, Lines(e.Variants), e.Answer, e.Category, Commas(e.Tags), Commas(e.Screens), e.Right, e.Link, e.IsActive);
    }

    public async Task<long> SaveAsync(FaqEntryDto dto, CancellationToken ct)
    {
        var question = (dto.Question ?? "").Trim();
        var answer = (dto.Answer ?? "").Trim().Replace("\r\n", "\n");
        if (question.Length == 0) throw new DomainException("Question is required.");
        if (question.Length > MaxQuestion) throw new DomainException($"Question must be at most {MaxQuestion} characters.");
        if (TextNormalizer.Tokens(question).Count == 0) throw new DomainException("The question needs at least one meaningful word (a screen, action or term).");
        if (answer.Length == 0) throw new DomainException("Answer is required.");
        if (answer.Length > MaxAnswer) throw new DomainException($"Answer must be at most {MaxAnswer} characters.");
        var variants = Clean(dto.Variants).Where(v => !v.Equals(question, StringComparison.OrdinalIgnoreCase)).ToList();
        if (variants.Count > MaxVariants) throw new DomainException($"At most {MaxVariants} other wordings are allowed.");
        if (variants.Any(v => v.Length > MaxQuestion)) throw new DomainException($"Each wording must be at most {MaxQuestion} characters.");
        if (variants.FirstOrDefault(v => TextNormalizer.Tokens(v).Count == 0) is { } empty)
            throw new DomainException($"The wording \"{empty}\" has no meaningful words to match on.");
        var screens = Clean(dto.Screens);
        if (screens.FirstOrDefault(s => !Screens.All.Contains(s)) is { } bad) throw new DomainException($"Unknown screen '{bad}'.");
        if (dto.Right == Right.None || (dto.Right & ~Right.All) != 0) throw new DomainException("Choose the right a user needs to see this answer.");
        var link = string.IsNullOrWhiteSpace(dto.Link) ? null : dto.Link.Trim();
        if (link is not null && (!link.StartsWith('/') || link.StartsWith("//") || link.Length > 200 || link.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\'')))
            throw new DomainException("Open-screen link must be an app path such as /settings/roles.");
        var category = string.IsNullOrWhiteSpace(dto.Category) ? "General" : dto.Category.Trim();
        if (category.Length > 60) throw new DomainException("Category must be at most 60 characters.");
        var variantsText = string.Join('\n', variants);
        var tagsText = string.Join(", ", Clean(dto.Tags));
        if (variantsText.Length > 4000 || tagsText.Length > 1000) throw new DomainException("Too many wordings or keywords.");

        var e = dto.Id > 0 ? await db.FaqEntries.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw new NotFoundException("Question not found.") : new FaqEntry();
        e.Question = question; e.Variants = variantsText; e.Answer = answer; e.Category = category; e.Tags = tagsText;
        e.Screens = string.Join(",", screens); e.Right = dto.Right; e.Link = link; e.IsActive = dto.IsActive;
        if (dto.Id == 0) db.FaqEntries.Add(e);
        audit.Log(Screens.Faq, dto.Id == 0 ? "Add" : "Edit", question);
        await db.SaveChangesAsync(ct);
        return e.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var e = await db.FaqEntries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Question not found.");
        db.FaqEntries.Remove(e);
        audit.Log(Screens.Faq, "Delete", e.Question);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<FaqLogDto>> LogsAsync(CancellationToken ct) =>
        db.FaqQuestionLogs.AsNoTracking().OrderByDescending(l => l.Count).ThenByDescending(l => l.LastAskedAt).Take(MaxLogRows)
            .Select(l => new FaqLogDto(l.Id, l.Text, l.Reason, l.Count, l.LastUserName, l.LastAskedAt)).ToListAsync(ct);

    public async Task DismissLogAsync(long id, CancellationToken ct)
    {
        var l = await db.FaqQuestionLogs.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Not found.");
        db.FaqQuestionLogs.Remove(l);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Which answers a question matches, ignoring roles (for the manage screen).</summary>
    public async Task<FaqTestResult> TestAsync(string query, CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        return new FaqTestResult(engine.Understand(query ?? ""), AnswerEngine.AnswerThreshold, AnswerEngine.MaybeThreshold,
            engine.Search(query ?? "", 6).Select(m => new FaqTestMatch(m.Item.Id, m.Item.Question, m.Item.Category, m.Confidence, m.Item.BuiltIn, m.Item.Screens)).ToList());
    }

    // ---------------- helpers ----------------

    private static FaqItem ToItem(FaqEntry e) =>
        new("c:" + e.Id, e.Question, Lines(e.Variants), e.Answer, e.Category, Commas(e.Tags), Commas(e.Screens), e.Right, e.Link, BuiltIn: false);

    private static FaqItemDto ToDto(FaqItem i, long? entryId) =>
        new(i.Id, entryId, i.Question, i.Variants, i.Answer, i.Category, i.Tags, i.Screens, i.Right.ToString(), i.Link, i.BuiltIn);

    private static List<string> Lines(string? s) => (s ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static List<string> Commas(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? []).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
