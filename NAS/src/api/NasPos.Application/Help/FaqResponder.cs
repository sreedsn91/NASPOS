using NasPos.Domain;

namespace NasPos.Application.Help;

public sealed record FaqSuggestion(string Id, string Question);

/// <summary>
/// Chatbot reply. Kind: "answer" (sure), "maybe" (probably this), "restricted" (the topic belongs to screens the
/// user's role can't use), "unknown" (no answer yet) or "smalltalk".
/// </summary>
public sealed record FaqReply(string Kind, string Text, string? Id, string? Question, string? Category, string? Link,
    double Confidence, IReadOnlyList<FaqSuggestion> Related)
{
    /// <summary>Why the question should be logged for the admin, or null when it was answered. Server-side only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LogReason { get; init; }
}

/// <summary>
/// Role-aware reply logic, kept free of the database so it can be tested directly. The user only ever sees answers
/// for screens their role has rights to: a sales person asking about year-end closing is told it's outside their
/// role, without the closing steps.
/// </summary>
public static class FaqResponder
{
    /// <summary>How much better a hidden answer must match than the best visible one to report "outside your role".</summary>
    private const double RestrictedMargin = 0.08;

    private static readonly HashSet<string> Greetings = ["hi", "hello", "hey", "hai", "helo", "namaste", "namaskaram", "good", "morning", "afternoon", "evening", "there", "bot", "chatbot"];
    private static readonly HashSet<string> Thanks = ["thanks", "thank", "you", "thx", "ty", "ok", "okay", "great", "nice", "super", "good", "fine", "cool"];
    private static readonly HashSet<string> HelpWords = ["help", "menu", "topics", "topic", "start", "what", "can", "you", "do", "list", "questions", "categories"];

    /// <summary>Friendly names for permission screens, used when telling a user a topic is outside their role.</summary>
    public static readonly IReadOnlyDictionary<string, string> ScreenNames = new Dictionary<string, string>
    {
        ["Dashboard"] = "Dashboard", ["Company"] = "Company & backup", ["Settings"] = "Settings", ["Users"] = "Users",
        ["Roles"] = "Roles & rights", ["UserLog"] = "User log", ["AccountGroup"] = "Account groups", ["Ledger"] = "Ledgers",
        ["Customer"] = "Customers", ["Supplier"] = "Suppliers", ["Employee"] = "Employees", ["Product"] = "Products",
        ["Masters"] = "Categories, manufacturers & areas", ["Loyalty"] = "Loyalty schemes", ["Offer"] = "Item offers",
        ["PriceList"] = "Price lists", ["Bom"] = "Bill of materials", ["Sales"] = "Sales invoices", ["POS"] = "Touch POS",
        ["SalesReturn"] = "Sales returns", ["Estimate"] = "Estimates", ["SalesOrder"] = "Sales orders", ["DeliveryNote"] = "Delivery notes",
        ["Purchase"] = "Purchases", ["PurchaseReturn"] = "Purchase returns", ["PurchaseOrder"] = "Purchase orders",
        ["ReceiptNote"] = "Receipt notes", ["OpeningStock"] = "Opening stock", ["StockAdjustment"] = "Stock adjustment",
        ["Production"] = "Production", ["Voucher"] = "Accounts vouchers", ["OpeningBalance"] = "Opening balances",
        ["SalesReports"] = "Sales reports", ["PurchaseReports"] = "Purchase reports", ["StockReports"] = "Stock reports",
        ["AccountReports"] = "Account reports", ["TaxReports"] = "GST / tax reports", ["YearEnd"] = "Year-end closing",
        ["Tools"] = "Tools (import, copy, reset)", ["Messaging"] = "SMS & e-mail", ["Printing"] = "Printing", ["Faq"] = "Manage FAQ",
    };

    /// <summary>True when the role may see the item: it has the item's right on at least one of its screens.</summary>
    public static bool IsVisible(FaqItem item, Func<string, Right, bool> can) =>
        item.Screens.Count == 0 || item.Screens.Any(s => can(s, item.Right));

    public static FaqReply Ask(AnswerEngine engine, string message, Func<string, Right, bool> can)
    {
        var text = (message ?? "").Trim();
        if (text.Length > 300) text = text[..300];
        var words = TextNormalizer.Words(text);
        bool Visible(FaqItem i) => IsVisible(i, can);

        if (words.Count == 0)
            return SmallTalk("Type a question about NAS POS Pro, for example \"How do I make a bill?\"");
        if (words.All(Greetings.Contains))
            return SmallTalk("Hello! I'm the NAS POS Pro help bot. Ask me how to do anything in the app — I'll answer for the screens your role can use.",
                Starters(engine, can));
        if (words.All(Thanks.Contains) && words.Any(w => w is "thanks" or "thank" or "thx" or "ty"))
            return SmallTalk("You're welcome! Ask me anything else about NAS POS Pro.");
        if (words.All(HelpWords.Contains))
            return SmallTalk("I can help with: " + string.Join(", ", Categories(engine, can)) +
                             ".\nPick a suggestion below or type your own question.", Starters(engine, can));
        if (TextNormalizer.Tokens(text).Count == 0)
            return SmallTalk("Could you ask a full question? For example \"How do I cancel a bill?\" or \"Where is the GST report?\"", Starters(engine, can));

        var all = engine.Search(text, 30);
        var visible = all.Where(m => Visible(m.Item)).ToList();
        var best = visible.FirstOrDefault();
        var bestHidden = all.FirstOrDefault(m => !Visible(m.Item));

        // The closest answer belongs to screens this role can't use: say so, without the content. A visible answer
        // only wins when it matches about as well — a loosely related general answer must not stand in for it.
        if (bestHidden is not null && bestHidden.Confidence >= AnswerEngine.AnswerThreshold
            && (best is null || bestHidden.Confidence - best.Confidence >= RestrictedMargin))
        {
            var areas = string.Join(", ", bestHidden.Item.Screens.Select(s => ScreenNames.GetValueOrDefault(s, s)).Distinct());
            var needs = bestHidden.Item.Right == Right.Read ? "access to" : $"the {bestHidden.Item.Right} right on";
            return new FaqReply("restricted",
                $"That's about **{areas}**, and your role doesn't have {needs} it, so I can't help with that one. " +
                "If you need it for your work, please ask your administrator to give your role this right (Settings › Roles & Rights).",
                null, null, bestHidden.Item.Category, null, bestHidden.Confidence,
                visible.Where(m => m.Confidence >= 0.3).Take(3).Select(Suggest).ToList()) { LogReason = "no-access" };
        }

        if (best is null || best.Confidence < AnswerEngine.MaybeThreshold)
        {
            var close = visible.Where(m => m.Confidence >= 0.2).Take(3).Select(Suggest).ToList();
            var reply = close.Count > 0
                ? "Sorry, I don't have an answer for that yet. Did you mean one of these?"
                : "Sorry, I don't have an answer for that yet. I've noted your question for the administrator. Try other words, type \"help\" to see topics, or use FAQ › Email Us.";
            return new FaqReply("unknown", reply, null, null, null, null, best?.Confidence ?? 0, close) { LogReason = "no-match" };
        }

        var related = visible.Skip(1).Where(m => m.Confidence >= 0.3).Take(3).Select(Suggest).ToList();
        var kind = best.Confidence >= AnswerEngine.AnswerThreshold ? "answer" : "maybe";
        return new FaqReply(kind, best.Item.Answer, best.Item.Id, best.Item.Question, best.Item.Category, best.Item.Link, best.Confidence, related);
    }

    /// <summary>The answer for a suggestion the user clicked — still checked against the role.</summary>
    public static FaqReply? AnswerById(AnswerEngine engine, string id, Func<string, Right, bool> can)
    {
        var item = engine.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || !IsVisible(item, can)) return null;
        var related = engine.Search(item.Question, 8)
            .Where(m => m.Item.Id != id && m.Confidence >= 0.3 && IsVisible(m.Item, can)).Take(3).Select(Suggest).ToList();
        return new FaqReply("answer", item.Answer, item.Id, item.Question, item.Category, item.Link, 1, related);
    }

    /// <summary>
    /// Starter questions for this role: first one per category among answers tied to screens the role can use
    /// (its own work), then general ones.
    /// </summary>
    public static IReadOnlyList<FaqSuggestion> Starters(AnswerEngine engine, Func<string, Right, bool> can, int take = 8)
    {
        var visible = engine.Items.Where(i => IsVisible(i, can)).ToList();
        var ownWork = visible.Where(i => i.Screens.Count > 0).GroupBy(i => i.Category).Select(g => g.First());
        var general = visible.Where(i => i.Screens.Count == 0).GroupBy(i => i.Category).Select(g => g.First());
        return ownWork.Concat(general).DistinctBy(i => i.Id).Take(take).Select(i => new FaqSuggestion(i.Id, i.Question)).ToList();
    }

    public static IReadOnlyList<string> Categories(AnswerEngine engine, Func<string, Right, bool> can) =>
        engine.Items.Where(i => IsVisible(i, can)).Select(i => i.Category).Distinct().ToList();

    private static FaqSuggestion Suggest(FaqMatch m) => new(m.Item.Id, m.Item.Question);

    private static FaqReply SmallTalk(string text, IReadOnlyList<FaqSuggestion>? suggestions = null) =>
        new("smalltalk", text, null, null, null, null, 1, suggestions ?? []);
}
