namespace Kshethra.Domain.Entities;

/// <summary>
/// A question/answer the company added to the FAQ chatbot (the built-in answers ship with the app and are not stored).
/// Visible only to users who have <see cref="Right"/> on at least one of <see cref="Screens"/>; no screens = everyone.
/// </summary>
public class FaqEntry : TenantEntity
{
    public string Question { get; set; } = "";
    /// <summary>Other wordings of the question, one per line.</summary>
    public string? Variants { get; set; }
    public string Answer { get; set; } = "";
    public string Category { get; set; } = "General";
    /// <summary>Extra search words, comma separated.</summary>
    public string? Tags { get; set; }
    /// <summary>Permission screens this answer belongs to, comma separated (empty = every user).</summary>
    public string? Screens { get; set; }
    public Right Right { get; set; } = Right.Read;
    /// <summary>Optional app route the answer can open, e.g. "/settings/roles".</summary>
    public string? Link { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A chatbot question that got no answer, or whose answer was marked unhelpful — for the admin to answer.</summary>
public class FaqQuestionLog : TenantEntity
{
    public string Text { get; set; } = "";
    /// <summary>"no-match", "not-helpful" or "no-access".</summary>
    public string Reason { get; set; } = "no-match";
    public int Count { get; set; } = 1;
    public string? LastUserName { get; set; }
    public DateTimeOffset LastAskedAt { get; set; }
}
