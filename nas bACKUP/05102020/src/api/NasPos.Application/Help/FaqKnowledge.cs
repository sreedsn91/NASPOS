using System.Text.Json;
using System.Text.Json.Serialization;
using NasPos.Application.Reports;
using NasPos.Domain;

namespace NasPos.Application.Help;

/// <summary>
/// The built-in answers: the curated list shipped inside the DLL (Help/faq.json) plus one generated answer per report,
/// so every report in the catalogue can be found by name and is shown only to roles that can run it.
/// </summary>
public static class FaqKnowledge
{
    private sealed record SeedFile(List<SeedEntry> Entries);

    private sealed record SeedEntry(
        string? Id, string Question, List<string>? Variants, string Answer, string? Category, List<string>? Tags,
        List<string>? Screens, [property: JsonConverter(typeof(JsonStringEnumConverter<Right>))] Right? Right, string? Link);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>The curated answers from the embedded faq.json.</summary>
    public static IReadOnlyList<FaqItem> Curated { get; } = LoadCurated();

    private static List<FaqItem> LoadCurated()
    {
        using var stream = typeof(FaqKnowledge).Assembly.GetManifestResourceStream("NasPos.Help.faq.json")
            ?? throw new InvalidOperationException("Embedded FAQ resource NasPos.Help.faq.json is missing.");
        var file = JsonSerializer.Deserialize<SeedFile>(stream, Json) ?? new SeedFile([]);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<FaqItem>();
        foreach (var e in file.Entries)
        {
            var id = "b:" + (e.Id ?? Slug(e.Question));
            for (var n = 2; !ids.Add(id); n++) id = "b:" + (e.Id ?? Slug(e.Question)) + "-" + n;
            items.Add(new FaqItem(id, e.Question, e.Variants ?? [], e.Answer, e.Category ?? "General", e.Tags ?? [],
                e.Screens ?? [], e.Right ?? Right.Read, e.Link));
        }
        return items;
    }

    /// <summary>One "where is this report" answer per report definition, visible to roles that can run it.</summary>
    public static IEnumerable<FaqItem> ForReports(IEnumerable<ReportDefinition> reports) =>
        reports.Select(r =>
        {
            var name = r.Title;
            var plain = StripBrackets(name);
            var description = string.IsNullOrWhiteSpace(r.Description) ? "" : $"\n\nIt shows: {r.Description}.";
            return new FaqItem(
                "r:" + r.Key,
                $"Where is the {plain} report?",
                [name, plain + " report", "show " + plain, "how to see " + plain],
                $"Open **Reports › All Reports** and pick **{name}** (in the {r.Group} section), or press **Ctrl+K** and type its name.{description}\n\n" +
                "1. Choose the dates and any filters.\n2. Click **Run**.\n3. Use **Excel**, **PDF / Print** or **E-mail** to save or share it.\n\n" +
                "Clicking a bill or ledger row in a report opens it.",
                "Reports",
                [r.Group, r.Key.Replace('-', ' ')],
                [r.Screen],
                Right.Read,
                "/reports/" + r.Key);
        });

    private static string StripBrackets(string s)
    {
        var i = s.IndexOf(" (", StringComparison.Ordinal);
        return (i > 0 ? s[..i] : s).Trim();
    }

    private static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
