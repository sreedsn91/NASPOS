using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NasChatbot.Knowledge;

public sealed class ChatbotOptions
{
    /// <summary>Folder for knowledge.json, unanswered.json and admin.json. Relative paths are under the content root.</summary>
    public string DataDir { get; set; } = "App_Data";
}

public sealed class KnowledgeValidationException(string message) : Exception(message);

/// <summary>
/// The question/answer list and the "unanswered" log, kept as JSON files on disk (no database, no internet).
/// All writes go through one lock and are written to a temp file first, so a crash never leaves a half-written file.
/// </summary>
public sealed class KnowledgeStore
{
    public const int MaxQuestion = 300, MaxAnswer = 6000, MaxVariants = 40, MaxTags = 40, MaxCategory = 60, MaxUnanswered = 500;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class KnowledgeFile { public int Version { get; set; } = 1; public List<QaEntry> Entries { get; set; } = []; }
    private sealed class SeedFile { public List<QaInput> Entries { get; set; } = []; }

    private readonly object _gate = new();
    private readonly string _knowledgePath;
    private readonly string _unansweredPath;
    private List<QaEntry> _entries;
    private List<UnansweredQuestion> _unanswered;
    private volatile AnswerEngine _engine;

    public KnowledgeStore(string dataDir, string? seedPath)
    {
        Directory.CreateDirectory(dataDir);
        DataDir = dataDir;
        _knowledgePath = Path.Combine(dataDir, "knowledge.json");
        _unansweredPath = Path.Combine(dataDir, "unanswered.json");

        if (File.Exists(_knowledgePath))
            _entries = Read<KnowledgeFile>(_knowledgePath)?.Entries ?? [];
        else
        {
            _entries = seedPath is not null && File.Exists(seedPath) ? FromInputs(Read<SeedFile>(seedPath)?.Entries ?? []) : [];
            SaveEntries();
        }
        _unanswered = File.Exists(_unansweredPath) ? Read<List<UnansweredQuestion>>(_unansweredPath) ?? [] : [];
        _engine = new AnswerEngine(_entries);
    }

    public string DataDir { get; }

    /// <summary>The matcher for the current knowledge base (swapped atomically after each change).</summary>
    public AnswerEngine Engine => _engine;

    public IReadOnlyList<QaEntry> All() { lock (_gate) return _entries.Select(Clone).ToList(); }

    public QaEntry? Get(string id) { lock (_gate) return _entries.FirstOrDefault(e => e.Id == id) is { } e ? Clone(e) : null; }

    public IReadOnlyList<string> Categories()
    {
        lock (_gate) return _entries.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public QaEntry Add(QaInput input)
    {
        var entry = ToEntry(input, NewId());
        lock (_gate)
        {
            _entries.Add(entry);
            Commit();
        }
        return Clone(entry);
    }

    public QaEntry? Update(string id, QaInput input)
    {
        var updated = ToEntry(input, id);
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return null;
            _entries[index] = updated;
            Commit();
        }
        return Clone(updated);
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.Id == id) == 0) return false;
            Commit();
            return true;
        }
    }

    /// <summary>Adds the given entries, or replaces the whole list when <paramref name="replace"/> is true. All-or-nothing.</summary>
    public int Import(IReadOnlyList<QaInput> inputs, bool replace)
    {
        var imported = FromInputs(inputs); // validates every row before anything changes
        lock (_gate)
        {
            if (replace) _entries = imported;
            else _entries.AddRange(imported);
            Commit();
        }
        return imported.Count;
    }

    // ---------- unanswered log ----------

    public void RecordUnanswered(string text, string reason)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxQuestion) text = text[..MaxQuestion];
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            var existing = _unanswered.FirstOrDefault(u => string.Equals(u.Text, text, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Count++;
                existing.LastAskedUtc = now;
                if (reason == "not-helpful") existing.Reason = reason;
            }
            else
            {
                _unanswered.Add(new UnansweredQuestion { Id = NewId(), Text = text, Count = 1, Reason = reason, FirstAskedUtc = now, LastAskedUtc = now });
                if (_unanswered.Count > MaxUnanswered)
                    _unanswered = _unanswered.OrderByDescending(u => u.LastAskedUtc).Take(MaxUnanswered).ToList();
            }
            Write(_unansweredPath, _unanswered);
        }
    }

    public IReadOnlyList<UnansweredQuestion> Unanswered()
    {
        lock (_gate) return _unanswered.OrderByDescending(u => u.Count).ThenByDescending(u => u.LastAskedUtc).ToList();
    }

    public bool DismissUnanswered(string id)
    {
        lock (_gate)
        {
            if (_unanswered.RemoveAll(u => u.Id == id) == 0) return false;
            Write(_unansweredPath, _unanswered);
            return true;
        }
    }

    // ---------- helpers ----------

    private void Commit()
    {
        SaveEntries();
        _engine = new AnswerEngine(_entries);
    }

    private void SaveEntries() => Write(_knowledgePath, new KnowledgeFile { Entries = _entries });

    private static List<QaEntry> FromInputs(IEnumerable<QaInput> inputs)
    {
        var list = new List<QaEntry>();
        var row = 0;
        foreach (var input in inputs)
        {
            row++;
            try { list.Add(ToEntry(input, NewId())); }
            catch (KnowledgeValidationException ex) { throw new KnowledgeValidationException($"Entry {row}: {ex.Message}"); }
        }
        return list;
    }

    internal static QaEntry ToEntry(QaInput input, string id)
    {
        var question = (input.Question ?? "").Trim();
        var answer = (input.Answer ?? "").Trim().Replace("\r\n", "\n");
        if (question.Length == 0) throw new KnowledgeValidationException("Question is required.");
        if (question.Length > MaxQuestion) throw new KnowledgeValidationException($"Question must be at most {MaxQuestion} characters.");
        if (answer.Length == 0) throw new KnowledgeValidationException("Answer is required.");
        if (answer.Length > MaxAnswer) throw new KnowledgeValidationException($"Answer must be at most {MaxAnswer} characters.");
        if (TextNormalizer.Tokens(question).Count == 0)
            throw new KnowledgeValidationException("Question needs at least one meaningful word (not only words like \"how\" or \"what\").");

        var variants = Clean(input.Variants, MaxQuestion)
            .Where(v => !string.Equals(v, question, StringComparison.OrdinalIgnoreCase)).ToList();
        if (variants.Count > MaxVariants) throw new KnowledgeValidationException($"At most {MaxVariants} other wordings are allowed.");
        if (variants.FirstOrDefault(v => TextNormalizer.Tokens(v).Count == 0) is { } empty)
            throw new KnowledgeValidationException($"The wording \"{empty}\" has no meaningful words to match on; add a specific word (a screen, action or term).");
        var tags = Clean(input.Tags, 60);
        if (tags.Count > MaxTags) throw new KnowledgeValidationException($"At most {MaxTags} keywords are allowed.");
        var category = string.IsNullOrWhiteSpace(input.Category) ? "General" : input.Category.Trim();
        if (category.Length > MaxCategory) throw new KnowledgeValidationException($"Category must be at most {MaxCategory} characters.");

        return new QaEntry { Id = id, Question = question, Variants = variants, Answer = answer, Category = category, Tags = tags, UpdatedUtc = DateTime.UtcNow };
    }

    private static List<string> Clean(List<string>? values, int maxLength)
    {
        var result = new List<string>();
        foreach (var raw in values ?? [])
        {
            var v = raw?.Trim() ?? "";
            if (v.Length == 0) continue;
            if (v.Length > maxLength) throw new KnowledgeValidationException($"\"{v[..Math.Min(40, v.Length)]}…\" is longer than {maxLength} characters.");
            if (!result.Contains(v, StringComparer.OrdinalIgnoreCase)) result.Add(v);
        }
        return result;
    }

    private static QaEntry Clone(QaEntry e) => new()
    {
        Id = e.Id, Question = e.Question, Variants = [.. e.Variants], Answer = e.Answer,
        Category = e.Category, Tags = [.. e.Tags], UpdatedUtc = e.UpdatedUtc,
    };

    internal static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static T? Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);

    internal static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, overwrite: true);
    }
}
