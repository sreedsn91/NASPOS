namespace NasChatbot.Knowledge;

/// <summary>
/// Offline question matcher. Each entry is indexed on its question, variants and tags ("head") and, more weakly,
/// on its answer text. A query's confidence for an entry blends how much of the query the entry covers with how much
/// of the entry's own question the query covers, both weighted by word rarity (IDF). Typos are corrected against the
/// known vocabulary. Immutable: the store builds a new engine whenever the knowledge base changes.
/// </summary>
public sealed class AnswerEngine
{
    /// <summary>At or above this the bot answers directly.</summary>
    public const double AnswerThreshold = 0.55;
    /// <summary>Between this and <see cref="AnswerThreshold"/> the bot answers but says it is not sure.</summary>
    public const double MaybeThreshold = 0.4;
    private const double AnswerFieldWeight = 0.35;
    private const double QueryCoverageWeight = 0.65;

    private sealed record Indexed(QaEntry Entry, List<HashSet<string>> Phrases, HashSet<string> Head, HashSet<string> Answer, HashSet<string> ExactPhrases);

    private readonly List<Indexed> _items;
    private readonly Dictionary<string, double> _idf;
    private readonly Dictionary<string, int> _df;
    private readonly Dictionary<string, int> _headDf;
    private readonly double _unknownWeight;

    public AnswerEngine(IEnumerable<QaEntry> entries)
    {
        _items = entries.Select(IndexEntry).ToList();
        _df = new Dictionary<string, int>(StringComparer.Ordinal);
        _headDf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            foreach (var t in item.Head.Union(item.Answer)) _df[t] = _df.GetValueOrDefault(t) + 1;
            foreach (var t in item.Head) _headDf[t] = _headDf.GetValueOrDefault(t) + 1;
        }

        var n = Math.Max(_items.Count, 1);
        _idf = _df.ToDictionary(kv => kv.Key, kv => Math.Log(1 + (n - kv.Value + 0.5) / (kv.Value + 0.5)));
        // A word the bot has never seen weighs like the rarest known word: an off-topic word is strong
        // evidence the question is about something the knowledge base doesn't cover.
        _unknownWeight = _idf.Count == 0 ? 1 : _idf.Values.Max();
    }

    public int Count => _items.Count;

    private static Indexed IndexEntry(QaEntry e)
    {
        var phrases = new List<HashSet<string>>();
        var exact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in e.Variants.Prepend(e.Question))
        {
            var tokens = TextNormalizer.Tokens(text);
            if (tokens.Count == 0) continue;
            phrases.Add([.. tokens]);
            exact.Add(Key(tokens));
        }
        var head = new HashSet<string>(phrases.SelectMany(p => p), StringComparer.Ordinal);
        foreach (var tag in e.Tags) head.UnionWith(TextNormalizer.Tokens(tag));
        var answer = new HashSet<string>(TextNormalizer.Tokens(e.Answer).Concat(TextNormalizer.Tokens(e.Category)), StringComparer.Ordinal);
        return new Indexed(e, phrases, head, answer, exact);
    }

    private static string Key(IEnumerable<string> tokens) => string.Join(' ', tokens.Order(StringComparer.Ordinal));

    /// <summary>Query tokens after typo correction against the known vocabulary.</summary>
    public List<string> Understand(string query)
    {
        var result = new List<string>();
        foreach (var token in TextNormalizer.Tokens(query))
        {
            var fixedToken = _df.ContainsKey(token) ? token : Correct(token) ?? token;
            if (!result.Contains(fixedToken)) result.Add(fixedToken);
        }
        return result;
    }

    /// <summary>Closest question/keyword word within 1 typo (2 for long words). Answer-only words are not
    /// candidates, so "weather" doesn't become "whether" just because an answer happens to use it. Short words and
    /// a changed first letter are left alone ("gold" must not become "old").</summary>
    private string? Correct(string token)
    {
        if (token.Length < 5 || token.Any(char.IsDigit)) return null;
        var max = token.Length >= 8 ? 2 : 1;
        string? best = null;
        int bestDist = int.MaxValue, bestDf = 0;
        void Consider(string word, string result)
        {
            if (word.Length < 4 || word[0] != token[0]) return;
            var dist = TextNormalizer.Distance(token, word, max);
            if (dist > max) return;
            var df = _headDf.GetValueOrDefault(result);
            if (dist < bestDist || (dist == bestDist && df > bestDf)) { best = result; bestDist = dist; bestDf = df; }
        }
        foreach (var word in _headDf.Keys) Consider(word, word);
        foreach (var (stem, canonical) in TextNormalizer.SynonymStems)
            if (_headDf.ContainsKey(canonical)) Consider(stem, canonical);
        return best;
    }

    /// <summary>Entries ranked by confidence (0–1), best first.</summary>
    public List<MatchResult> Search(string query, int take = 5)
    {
        var tokens = Understand(query);
        if (tokens.Count == 0 || _items.Count == 0) return [];

        var exactKey = Key(tokens);
        var weights = tokens.ToDictionary(t => t, t => _idf.TryGetValue(t, out var w) ? w : _unknownWeight);
        var total = weights.Values.Sum();

        var results = new List<MatchResult>();
        foreach (var item in _items)
        {
            double confidence;
            if (item.ExactPhrases.Contains(exactKey))
                confidence = 1.0;
            else
            {
                double hit = 0;
                foreach (var (t, w) in weights)
                {
                    if (item.Head.Contains(t)) hit += w;
                    else if (item.Answer.Contains(t)) hit += w * AnswerFieldWeight;
                }
                var queryCoverage = hit / total;
                if (queryCoverage <= 0) continue;

                // Best overlap with one stored wording, scored both ways (Dice): a short wording that is merely
                // contained in a longer query ("add new staff" inside "create new user roles") is not a perfect match.
                double phraseCoverage = 0;
                foreach (var phrase in item.Phrases)
                {
                    double shared = 0, phraseWeight = 0;
                    foreach (var t in phrase)
                    {
                        var w = _idf.GetValueOrDefault(t, _unknownWeight);
                        phraseWeight += w;
                        if (weights.TryGetValue(t, out var qw)) shared += qw;
                    }
                    if (phraseWeight > 0) phraseCoverage = Math.Max(phraseCoverage, 2 * shared / (phraseWeight + total));
                }
                confidence = QueryCoverageWeight * queryCoverage + (1 - QueryCoverageWeight) * phraseCoverage;
            }
            results.Add(new MatchResult(item.Entry, Math.Round(confidence, 3)));
        }

        return results
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.Entry.Question.Length)
            .Take(take)
            .ToList();
    }
}
