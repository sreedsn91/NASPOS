using System.Text.Json;
using FluentAssertions;
using NasChatbot.Knowledge;

namespace NasChatbot.Tests;

public class NormalizerTests
{
    [Theory]
    [InlineData("create", "created", "creating", "creates")]
    [InlineData("invoice", "invoices", "bill", "bills")]
    [InlineData("delete", "remove", "removed", "deleting")]
    [InlineData("customer", "customers", "client", "clients")]
    public void Word_forms_and_synonyms_fold_together(params string[] words)
    {
        words.Select(TextNormalizer.Canonical).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Stop_words_are_dropped_and_terms_joined()
    {
        TextNormalizer.Tokens("How do I take a back-up of the e-mail settings?")
            .Should().Equal("take", "backup", "email", "set");
    }

    [Fact]
    public void Malayalam_text_keeps_its_vowel_signs()
    {
        TextNormalizer.Words("മട്ട അരി").Should().Equal("മട്ട", "അരി");
    }

    [Theory]
    [InlineData("suplier", "supplier", 1)]
    [InlineData("retrun", "return", 1)]
    [InlineData("backup", "backup", 0)]
    public void Distance_counts_typos_including_swaps(string a, string b, int expected)
    {
        TextNormalizer.Distance(a, b, 2).Should().Be(expected);
    }
}

public class SeedMatchingTests
{
    private static readonly AnswerEngine Engine = new(LoadSeed());

    internal static List<QaEntry> LoadSeed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "seed", "knowledge.seed.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var inputs = doc.RootElement.GetProperty("entries").Deserialize<List<QaInput>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return inputs.Select((i, n) => KnowledgeStore.ToEntry(i, "e" + n)).ToList();
    }

    [Theory]
    [InlineData("Where I can create new user roles", "How do I create a new role?")]
    [InlineData("how to add new role", "How do I create a new role?")]
    [InlineData("how to add a user", "How do I add a new user?")]
    [InlineData("create login for my staff", "How do I add a new user?")]
    [InlineData("how to cancel an invoice", "How do I cancel a bill?")]
    [InlineData("delete bill", "How do I cancel a bill?")]
    [InlineData("gstr 1", "How do I get the GSTR-1 report?")]
    [InlineData("how do i take backup", "How do I take a backup?")]
    [InlineData("shortcut keys", "What are the keyboard shortcuts?")]
    [InlineData("how to retrun goods to suplier", "How do I return goods to a supplier?")]
    [InlineData("customer paid with gpay", "How do I take card or UPI payment?")]
    [InlineData("credit limit exceeded", "Error: Credit limit exceeded")]
    [InlineData("add new item", "How do I add a new product?")]
    [InlineData("close financial year", "How do I close the financial year?")]
    [InlineData("cash deposited to bank", "How do I record cash deposited in the bank?")]
    [InlineData("print barcode stickers", "How do I print barcode labels?")]
    [InlineData("change bill number prefix", "How do I change the bill number prefix?")]
    [InlineData("forgot password account locked", "My account is locked after wrong passwords")]
    [InlineData("how to sell on credit to rajesh", "How do I make a credit bill?")]
    [InlineData("customer returned 1 rice", "How do I take back goods from a customer?")]
    [InlineData("how to make invocie on credit", "How do I make a credit bill?")]
    [InlineData("bakup data", "How do I take a backup?")]
    public void Real_questions_find_the_right_answer(string asked, string expected)
    {
        var best = Engine.Search(asked).First();
        best.Entry.Question.Should().Be(expected);
        best.Confidence.Should().BeGreaterThanOrEqualTo(AnswerEngine.MaybeThreshold);
    }

    [Theory]
    [InlineData("what is the weather in kochi today")]
    [InlineData("how do I fly an aeroplane")]
    [InlineData("tell me a joke about cricket")]
    [InlineData("what is the price of gold")]
    [InlineData("who won the football match")]
    public void Unrelated_questions_are_not_answered(string asked)
    {
        var best = Engine.Search(asked).FirstOrDefault();
        (best?.Confidence ?? 0).Should().BeLessThan(AnswerEngine.MaybeThreshold);
    }

    [Fact]
    public void Every_stored_wording_finds_its_own_answer()
    {
        var entries = LoadSeed();
        var engine = new AnswerEngine(entries);
        var misses = new List<string>();
        foreach (var e in entries)
            foreach (var wording in e.Variants.Prepend(e.Question))
            {
                var best = engine.Search(wording).FirstOrDefault();
                if (best is null || best.Confidence < 1.0 && best.Entry.Id != e.Id)
                    misses.Add($"\"{wording}\" → \"{best?.Entry.Question}\" ({best?.Confidence})");
            }
        misses.Should().BeEmpty("each wording should lead to its own entry (or one with identical wording)");
    }

    [Fact]
    public void Exact_question_text_is_full_confidence()
    {
        Engine.Search("How do I take a backup?").First().Confidence.Should().Be(1.0);
    }
}
