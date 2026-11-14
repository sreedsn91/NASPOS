using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kshethra.Application.Help;

/// <summary>
/// Turns free text into comparable search tokens: lower-case, joined multi-word terms, stop words dropped,
/// a light English stemmer, and synonyms folded onto one canonical word ("invoice" → "bill").
/// Everything is local and deterministic — no internet, no model.
/// </summary>
public static partial class TextNormalizer
{
    // Multi-word terms that must stay one token, applied before splitting.
    private static readonly (Regex Pattern, string Replacement)[] Phrases =
    [
        (Rx(@"\be[\s-]?mail\b"), "email"),
        (Rx(@"\b(log|sign)[\s-]?(in|on)\b"), "login"),
        (Rx(@"\b(log|sign)[\s-]?(out|off)\b"), "logout"),
        (Rx(@"\bback[\s-]up\b"), "backup"),
        (Rx(@"\bbar[\s-]code\b"), "barcode"),
        (Rx(@"\bset[\s-]up\b"), "setup"),
        (Rx(@"\bgstr[\s-]?1\b"), "gstr1"),
        (Rx(@"\bgstr[\s-]?3b\b"), "gstr3b"),
        (Rx(@"\byear[\s-]end\b"), "yearend"),
        (Rx(@"\bday[\s-]book\b"), "daybook"),
        (Rx(@"\bend[\s-]of[\s-]day\b"), "endofday"),
        (Rx(@"\bprofit\s*(&|and)\s*loss\b|\bp\s*&\s*l\b"), "profitloss"),
        (Rx(@"\btrial[\s-]balance\b"), "trialbalance"),
        (Rx(@"\bbalance[\s-]sheet\b"), "balancesheet"),
        (Rx(@"\bopening[\s-]stock\b"), "openingstock"),
        (Rx(@"\bopening[\s-]balances?\b"), "openingbalance"),
        (Rx(@"\bcredit[\s-]limit\b"), "creditlimit"),
        (Rx(@"\bcredit[\s-]note\b"), "creditnote"),
        (Rx(@"\bdebit[\s-]note\b"), "debitnote"),
        (Rx(@"\bprice[\s-]lists?\b"), "pricelist"),
        (Rx(@"\bbill[\s-]of[\s-]materials?\b"), "bom"),
        (Rx(@"\bsales[\s-]?person\b|\bsales[\s-]?man\b"), "salesperson"),
        (Rx(@"\bctrl\s*\+\s*"), "ctrl"),
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "is", "are", "am", "was", "were", "be", "been", "being", "do", "does", "did", "done",
        "i", "me", "my", "we", "us", "our", "you", "your", "it", "its", "this", "that", "these", "those", "there", "here",
        "to", "of", "in", "on", "at", "for", "from", "by", "with", "into", "onto", "about", "as", "and", "or", "but", "if", "then", "so",
        "how", "what", "where", "when", "which", "who", "whom", "why", "can", "could", "should", "would", "will", "shall", "may", "might", "must",
        "please", "pls", "plz", "kindly", "tell", "show", "explain", "know", "want", "wanna", "need", "like", "get", "got", "let", "also",
        "any", "some", "way", "ways", "option", "possible", "able", "help", "hi", "hello", "hey", "thanks", "thank",
        "app", "application", "software", "program", "system", "nas", "pro", "screen", "page", "one", "it's", "im", "i'm",
        "dont", "don't", "doesnt", "doesn't", "cant", "can't", "cannot", "not", "no", "ok", "okay", "just", "now", "still", "using", "use",
    };

    // Each group folds onto its first word. Words are stemmed at start-up, so plurals and -ing forms are covered.
    private static readonly string[][] SynonymGroups =
    [
        ["bill", "invoice", "billing", "invoicing"],
        ["create", "add", "new", "make", "register", "insert", "setup", "enter", "entry"],
        ["delete", "remove", "erase", "drop"],
        ["cancel", "void", "undo", "reverse"],
        ["edit", "change", "modify", "update", "correct", "alter", "amend", "rename"],
        ["customer", "client", "buyer", "debtor"],
        ["supplier", "vendor", "distributor", "creditor"],
        ["product", "item", "goods", "article", "sku", "material"],
        ["role", "permission", "right", "access", "privilege", "rights"],
        ["user", "staff", "operator"],
        ["password", "pwd", "passcode", "passwd"],
        ["print", "printer", "printout", "printing"],
        ["gst", "tax", "taxes", "vat"],
        ["stock", "inventory", "inventories"],
        ["price", "rate"],
        ["discount", "rebate"],
        ["scale", "weighing", "weigh", "weight"],
        ["shortcut", "hotkey", "keyboard", "shortcutkey"],
        ["error", "problem", "issue", "fail", "failed", "failing", "wrong", "trouble", "bug"],
        ["outstanding", "due", "dues", "pending", "receivable", "unpaid"],
        ["company", "temple", "shop", "store", "business", "firm", "branch"],
        ["receipt", "receive", "received", "collect", "collection"],
        ["return", "refund", "returned"],
        ["loyalty", "point", "reward", "rewards"],
        ["upi", "gpay", "phonepe", "paytm", "googlepay"],
        ["login", "signin", "logon"],
        ["cheque", "chq"],
        ["year", "fy"],
        ["offer", "promotion", "promo"],
        ["salesperson", "salesmen"],
        ["employee", "worker"],
        ["find", "search", "look", "locate", "lookup"],
        ["open", "go", "navigate", "reach", "launch"],
        ["number", "numbering", "serial"],
        ["expiry", "expire", "expired", "expiring", "expiration"],
        ["lock", "locked", "block", "blocked"],
        ["theme", "colour", "color", "dark", "appearance"],
        ["sale", "sell", "sold", "selling"],
        ["purchase", "buy", "bought", "buying", "purchasing"],
        ["counter", "till", "terminal"],
        ["quotation", "quote", "estimate"],
    ];

    private static readonly Dictionary<string, string> Synonyms = BuildSynonyms();

    /// <summary>Stemmed synonym words and the canonical token each folds to (lets typos of "invoice" reach "bill").</summary>
    public static IReadOnlyDictionary<string, string> SynonymStems => Synonyms;

    private static Dictionary<string, string> BuildSynonyms()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in SynonymGroups)
        {
            var canonical = Stem(group[0]);
            foreach (var word in group)
                map.TryAdd(Stem(word), canonical);
        }
        return map;
    }

    /// <summary>Lower-case words in their original form (for greetings and exact-phrase checks).</summary>
    public static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return words;
        var lowered = text.ToLowerInvariant();
        foreach (var (pattern, replacement) in Phrases)
            lowered = pattern.Replace(lowered, replacement);

        var sb = new StringBuilder();
        foreach (var ch in lowered)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            // Keep letters, digits and combining marks (Malayalam/Indic vowel signs are marks, not letters).
            if (char.IsLetterOrDigit(ch) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                sb.Append(ch);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    /// <summary>Search tokens: words minus stop words, stemmed, synonyms folded. Order kept, duplicates removed.</summary>
    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        foreach (var word in Words(text))
        {
            if (StopWords.Contains(word)) continue;
            var token = Canonical(word);
            if (token.Length == 0 || tokens.Contains(token)) continue;
            tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>Stem one word and fold it onto its synonym group.</summary>
    public static string Canonical(string word)
    {
        var stem = Stem(word);
        return Synonyms.TryGetValue(stem, out var canonical) ? canonical : stem;
    }

    /// <summary>A small, predictable English suffix stripper. create/created/creating/creates all become "creat".</summary>
    internal static string Stem(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length <= 3 || !IsAsciiWord(w)) return w;

        if (w.EndsWith("ies") && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses")) w = w[..^2];
        else if (w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes")) w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) w = w[..^1];

        if (w.EndsWith("ing") && w.Length > 5) w = Undouble(w[..^3]);
        else if (w.EndsWith("ed") && w.Length > 4) w = Undouble(w[..^2]);

        if (w.EndsWith('e') && w.Length > 4) w = w[..^1];
        return w;
    }

    private static string Undouble(string w) =>
        w.Length > 3 && w[^1] == w[^2] && w[^1] is not ('l' or 's' or 'z') ? w[..^1] : w;

    private static bool IsAsciiWord(string w)
    {
        foreach (var c in w) if (c is < 'a' or > 'z') return false;
        return true;
    }

    /// <summary>Edit distance with adjacent swaps (Damerau–Levenshtein, optimal string alignment), capped for speed.</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
