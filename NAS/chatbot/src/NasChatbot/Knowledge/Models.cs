namespace NasChatbot.Knowledge;

/// <summary>One question/answer pair the bot can reply with.</summary>
public sealed class QaEntry
{
    public string Id { get; set; } = "";
    /// <summary>The main wording of the question, shown to users as a suggestion.</summary>
    public string Question { get; set; } = "";
    /// <summary>Other ways people ask the same thing; all of them are matched.</summary>
    public List<string> Variants { get; set; } = [];
    public string Answer { get; set; } = "";
    public string Category { get; set; } = "General";
    /// <summary>Extra search words (screen names, menu paths, error text).</summary>
    public List<string> Tags { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A question the bot could not answer (or a user marked unhelpful), kept so the admin can add an answer.</summary>
public sealed class UnansweredQuestion
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public int Count { get; set; }
    /// <summary>"no-match" or "not-helpful".</summary>
    public string Reason { get; set; } = "no-match";
    public DateTime FirstAskedUtc { get; set; }
    public DateTime LastAskedUtc { get; set; }
}

public sealed record QaInput(string Question, List<string>? Variants, string Answer, string? Category, List<string>? Tags);

public sealed record MatchResult(QaEntry Entry, double Confidence);

public sealed record Suggestion(string Id, string Question);

public sealed record ChatReply(
    string Kind,             // "answer", "maybe", "unknown", "smalltalk"
    string Text,
    string? EntryId,
    string? Question,
    string? Category,
    double Confidence,
    IReadOnlyList<Suggestion> Related);
