using FluentAssertions;
using Kshethra.Application.Common;
using Kshethra.Application.Help;
using Kshethra.Application.Reports;
using Kshethra.Domain;

namespace Kshethra.Tests.Domain;

public class FaqTests
{
    private static readonly AnswerEngine Engine = new(FaqKnowledge.Curated.Concat(FaqKnowledge.ForReports(
    [
        new ReportDefinition("stock-summary", "Stock Summary (Valuation)", "Stock", Screens.StockReports, [], "On-hand quantity and value at cost"),
        new ReportDefinition("end-of-day", "End of Day", "Counter", Screens.SalesReports, []),
    ])));

    private static Func<string, Right, bool> Role(params (string Screen, Right Rights)[] rights) =>
        (screen, right) => rights.Any(r => r.Screen == screen && (r.Rights & right) == right);

    private static readonly Func<string, Right, bool> Admin = (_, _) => true;
    private static readonly Func<string, Right, bool> Salesperson = Role(
        ("POS", Right.Read | Right.Add | Right.Print), ("Sales", Right.Read | Right.Add | Right.Print),
        ("SalesReturn", Right.Read | Right.Add), ("Customer", Right.Read | Right.Add));
    private static readonly Func<string, Right, bool> Accountant = Role(
        ("Voucher", Right.All), ("OpeningBalance", Right.All), ("AccountReports", Right.Read), ("TaxReports", Right.Read));

    [Theory]
    [InlineData("Where I can create new user roles", "b:roles-create")]
    [InlineData("how do I take backup", "b:backup")]
    [InlineData("gstr 1", "b:gstr1")]
    [InlineData("how to cancel an invoice", "b:cancel-bill")]
    [InlineData("how to retrun goods to suplier", "b:purchase-return")]
    [InlineData("stock summary report", "r:stock-summary")]
    [InlineData("contact support", "b:email-us")]
    public void Admin_gets_the_right_answer(string asked, string expectedId)
    {
        var reply = FaqResponder.Ask(Engine, asked, Admin);
        reply.Kind.Should().BeOneOf("answer", "maybe");
        reply.Id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("how do I make a bill in POS", "b:pos-bill")]
    [InlineData("customer paid with gpay", "b:card-upi")]
    [InlineData("customer returned an item", "b:sales-return")]
    [InlineData("add new customer", "b:customer-add")]
    [InlineData("how do I change my password", "b:password-change")]
    public void Salesperson_gets_answers_for_their_own_work(string asked, string expectedId)
    {
        var reply = FaqResponder.Ask(Engine, asked, Salesperson);
        reply.Kind.Should().BeOneOf("answer", "maybe");
        reply.Id.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("how do I close the financial year", "Year-end closing")]
    [InlineData("how do I create a new role", "Roles & rights")]
    [InlineData("how to cancel a bill", "Cancel")]
    [InlineData("how do I take a backup", "Temple & backup")]
    [InlineData("stock summary report", "Stock reports")]
    public void Salesperson_is_told_when_a_topic_is_outside_their_role(string asked, string mentions)
    {
        var reply = FaqResponder.Ask(Engine, asked, Salesperson);
        reply.Kind.Should().Be("restricted");
        reply.Text.Should().Contain(mentions);
        reply.Id.Should().BeNull("the hidden answer's content must not be sent");
        reply.LogReason.Should().Be("no-access");
    }

    [Fact]
    public void Accountant_sees_accounts_but_not_billing()
    {
        FaqResponder.Ask(Engine, "how do I record money received from a customer", Accountant).Id.Should().Be("b:receipt");
        FaqResponder.Ask(Engine, "how do I make a bill in POS", Accountant).Kind.Should().Be("restricted");
    }

    [Fact]
    public void Suggestions_and_topics_only_include_what_the_role_can_see()
    {
        var starters = FaqResponder.Starters(Engine, Salesperson);
        starters.Should().NotBeEmpty();
        foreach (var s in starters)
            FaqResponder.IsVisible(Engine.Items.Single(i => i.Id == s.Id), Salesperson).Should().BeTrue(s.Question);
        FaqResponder.Categories(Engine, Salesperson).Should().Contain("Sales & billing").And.NotContain("Backup & year end");
        FaqResponder.AnswerById(Engine, "b:year-end", Salesperson).Should().BeNull();
        FaqResponder.AnswerById(Engine, "b:year-end", Admin).Should().NotBeNull();
    }

    [Theory]
    [InlineData("what is the weather in kochi today")]
    [InlineData("what is the price of gold")]
    [InlineData("who won the football match")]
    public void Unrelated_questions_are_not_answered(string asked)
    {
        var reply = FaqResponder.Ask(Engine, asked, Admin);
        reply.Kind.Should().Be("unknown");
        reply.LogReason.Should().Be("no-match");
    }

    [Fact]
    public void Built_in_answers_are_well_formed()
    {
        var items = FaqKnowledge.Curated;
        items.Should().HaveCountGreaterThan(90);
        items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        foreach (var i in items)
        {
            i.Screens.Where(s => !Screens.All.Contains(s)).Should().BeEmpty(i.Id);
            (i.Link is null || i.Link.StartsWith('/')).Should().BeTrue(i.Id);
            TextNormalizer.Tokens(i.Question).Should().NotBeEmpty(i.Id);
            i.Variants.Where(v => TextNormalizer.Tokens(v).Count == 0).Should().BeEmpty(i.Id);
        }
    }

    [Fact]
    public void Every_stored_wording_finds_its_own_answer()
    {
        var engine = new AnswerEngine(FaqKnowledge.Curated);
        var misses = new List<string>();
        foreach (var item in FaqKnowledge.Curated)
            foreach (var wording in item.Variants.Prepend(item.Question))
            {
                var best = engine.Search(wording).FirstOrDefault();
                if (best is null || (best.Item.Id != item.Id && best.Confidence < 1.0))
                    misses.Add($"\"{wording}\" ({item.Id}) → {best?.Item.Id} {best?.Confidence}");
            }
        misses.Should().BeEmpty();
    }
}
