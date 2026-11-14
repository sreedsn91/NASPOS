namespace NasChatbot.Knowledge;

/// <summary>Turns a user's message into a reply: small talk, a confident answer, a "did you mean", or "I don't know yet".</summary>
public sealed class ChatService(KnowledgeStore store)
{
    private static readonly HashSet<string> Greetings = ["hi", "hello", "hey", "hai", "helo", "namaste", "namaskaram", "good", "morning", "afternoon", "evening", "there", "bot", "chatbot"];
    private static readonly HashSet<string> Thanks = ["thanks", "thank", "you", "thx", "ty", "ok", "okay", "great", "nice", "super", "good", "fine", "cool"];
    private static readonly HashSet<string> HelpWords = ["help", "menu", "topics", "topic", "start", "what", "can", "you", "do", "list", "questions", "categories"];

    public ChatReply Ask(string message)
    {
        var text = (message ?? "").Trim();
        if (text.Length > KnowledgeStore.MaxQuestion) text = text[..KnowledgeStore.MaxQuestion];
        var words = TextNormalizer.Words(text);

        if (words.Count == 0)
            return SmallTalk("Type a question about NAS POS Pro, for example \"How do I create a new role?\"");
        if (words.All(Greetings.Contains))
            return SmallTalk("Hello! I'm NAS Chatbot. Ask me anything about using NAS POS Pro — billing, stock, GST, accounts, users, backups and more.");
        if (words.All(Thanks.Contains) && (words.Contains("thanks") || words.Contains("thank") || words.Contains("thx") || words.Contains("ty")))
            return SmallTalk("You're welcome! Ask me anything else about NAS POS Pro.");
        if (words.All(HelpWords.Contains))
            return SmallTalk("I can answer questions on these topics: " + string.Join(", ", store.Categories()) +
                             ".\nPick one of the suggestions below, or type your own question.", Popular());
        if (TextNormalizer.Tokens(text).Count == 0)
            return SmallTalk("Could you ask a full question? For example \"How do I cancel a bill?\" or \"Where is the GST report?\"", Popular());

        var matches = store.Engine.Search(text, 6);
        var best = matches.FirstOrDefault();

        if (best is null || best.Confidence < AnswerEngine.MaybeThreshold)
        {
            store.RecordUnanswered(text, "no-match");
            var close = matches.Where(m => m.Confidence >= 0.2).Take(3).Select(ToSuggestion).ToList();
            var reply = close.Count > 0
                ? "Sorry, I don't have an answer for that yet. Did you mean one of these?"
                : "Sorry, I don't have an answer for that yet. I've noted your question so the admin can add an answer. Try different words, or type \"help\" to see topics.";
            return new ChatReply("unknown", reply, null, null, null, best?.Confidence ?? 0, close);
        }

        var related = matches.Skip(1).Where(m => m.Confidence >= 0.3).Take(3).Select(ToSuggestion).ToList();
        var kind = best.Confidence >= AnswerEngine.AnswerThreshold ? "answer" : "maybe";
        return new ChatReply(kind, best.Entry.Answer, best.Entry.Id, best.Entry.Question, best.Entry.Category, best.Confidence, related);
    }

    /// <summary>Answer for a suggestion chip the user clicked (exact entry, no matching needed).</summary>
    public ChatReply? AnswerById(string id)
    {
        var entry = store.Get(id);
        if (entry is null) return null;
        var related = store.Engine.Search(entry.Question, 4).Where(m => m.Entry.Id != id && m.Confidence >= 0.3).Take(3).Select(ToSuggestion).ToList();
        return new ChatReply("answer", entry.Answer, entry.Id, entry.Question, entry.Category, 1, related);
    }

    /// <summary>Starter questions: one from each category, in category order.</summary>
    public IReadOnlyList<Suggestion> Popular(int take = 8) =>
        store.All().GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).Take(take).Select(e => new Suggestion(e.Id, e.Question)).ToList();

    private static Suggestion ToSuggestion(MatchResult m) => new(m.Entry.Id, m.Entry.Question);

    private ChatReply SmallTalk(string text, IReadOnlyList<Suggestion>? suggestions = null) =>
        new("smalltalk", text, null, null, null, 1, suggestions ?? []);
}
