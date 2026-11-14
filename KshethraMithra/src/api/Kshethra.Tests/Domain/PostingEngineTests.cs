using FluentAssertions;
using Kshethra.Domain;
using Kshethra.Domain.Calculation;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Posting;
using static Kshethra.Tests.Domain.CalculatorTests;

namespace Kshethra.Tests.Domain;

public class PostingEngineTests
{
    private const long Customer = 1001, Supplier = 2001, CardBank = 3001;
    // System ledgers map to 100 + enum value so assertions can name them.
    private static long L(SystemLedger s) => 100 + (int)s;
    private static readonly PostingContext Ctx = new() { Ledger = L };

    private static decimal Net(PostingResult r, long ledger) => r.Legs.Where(l => l.LedgerId == ledger).Sum(l => l.Debit - l.Credit);

    private static TradeDocument Calc(TradeDocument d) { DocumentCalculator.Calculate(d); return d; }

    [Fact]
    public void Cash_sale_debits_cash_and_credits_sales_and_output_tax()
    {
        var d = Calc(Doc(lines: Line(2, 100, cost: 60)));
        var r = PostingEngine.PostTrade(d, Ctx);
        r.TotalDebit.Should().Be(r.TotalCredit);
        Net(r, L(SystemLedger.Cash)).Should().Be(236);
        Net(r, L(SystemLedger.Sales)).Should().Be(-200);
        Net(r, L(SystemLedger.CGSTOutput)).Should().Be(-18);
        Net(r, L(SystemLedger.SGSTOutput)).Should().Be(-18);
        r.Stock.Should().ContainSingle().Which.QtyOut.Should().Be(2);
    }

    [Fact]
    public void Credit_sale_debits_customer_and_requires_one()
    {
        var d = Calc(Doc(lines: Line(1, 100)));
        d.PaymentMode = PaymentMode.Credit;
        var act = () => PostingEngine.PostTrade(d, Ctx);
        act.Should().Throw<DomainException>().Where(e => e.Code == "party_required");
        d.PartyLedgerId = Customer;
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, Customer).Should().Be(118);
    }

    [Fact]
    public void Mixed_sale_splits_card_cash_and_credit()
    {
        var d = Calc(Doc(lines: Line(1, 1000, gst: 0)));
        d.PaymentMode = PaymentMode.Mixed;
        d.PartyLedgerId = Customer;
        d.CardLedgerId = CardBank;
        d.CardAmount = 500; d.CashAmount = 300; d.CreditAmount = 200;
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, Customer).Should().Be(200);
        Net(r, CardBank).Should().Be(500);
        Net(r, L(SystemLedger.Cash)).Should().Be(300);
        Net(r, L(SystemLedger.Sales)).Should().Be(-1000);
    }

    [Fact]
    public void Mixed_sale_split_must_equal_bill()
    {
        var d = Calc(Doc(lines: Line(1, 1000, gst: 0)));
        d.PaymentMode = PaymentMode.Mixed;
        d.CardAmount = 500; d.CashAmount = 300;
        var act = () => PostingEngine.PostTrade(d, Ctx);
        act.Should().Throw<DomainException>().Where(e => e.Code == "payment_split");
    }

    [Fact]
    public void Mixed_sale_without_customer_moves_card_part_from_cash_to_bank()
    {
        var d = Calc(Doc(lines: Line(1, 1000, gst: 0)));
        d.PaymentMode = PaymentMode.Mixed;
        d.CardAmount = 400; d.CashAmount = 600;
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, L(SystemLedger.Cash)).Should().Be(600);
        Net(r, L(SystemLedger.Bank)).Should().Be(400);
    }

    [Fact]
    public void Inter_state_sale_posts_igst()
    {
        var d = Calc(Doc(tax: TaxType.InterState, lines: Line(1, 100, gst: 18)));
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, L(SystemLedger.IGSTOutput)).Should().Be(-18);
        Net(r, L(SystemLedger.CGSTOutput)).Should().Be(0);
    }

    [Fact]
    public void Round_off_cash_discount_and_cess_are_booked()
    {
        var d = Doc(lines: Line(1, 99.70m, gst: 0, cess: 0, kf: 1));
        d.RoundOffMode = RoundOffMode.Auto;
        d.CashDiscount = 5;
        Calc(d);
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, L(SystemLedger.KFCessOutput)).Should().Be(-1.00m);
        Net(r, L(SystemLedger.CashDiscount)).Should().Be(5);
        Net(r, L(SystemLedger.Cash)).Should().Be(d.BillAmount);
        Net(r, L(SystemLedger.RoundOff)).Should().Be(-d.RoundOff);
    }

    [Fact]
    public void Sales_return_mirrors_sale_and_brings_stock_back()
    {
        var d = Calc(Doc(VoucherType.SalesReturn, lines: Line(1, 100)));
        d.PaymentMode = PaymentMode.Credit; d.PartyLedgerId = Customer;
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, Customer).Should().Be(-118);
        Net(r, L(SystemLedger.SalesReturn)).Should().Be(100);
        Net(r, L(SystemLedger.CGSTOutput)).Should().Be(9);
        r.Stock.Single().QtyIn.Should().Be(1);
    }

    [Fact]
    public void Purchase_debits_purchase_and_input_tax_and_credits_supplier()
    {
        var d = Doc(VoucherType.Purchase, lines: Line(10, 10, free: 2));
        d.PaymentMode = PaymentMode.Credit; d.PartyLedgerId = Supplier;
        d.Freight = 50; d.FreightTaxRate = 18; d.OtherExpense = 10;
        Calc(d);
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, Supplier).Should().Be(-d.BillAmount);
        Net(r, L(SystemLedger.Purchase)).Should().Be(100);
        Net(r, L(SystemLedger.FreightCoolie)).Should().Be(50);
        Net(r, L(SystemLedger.OtherExpense)).Should().Be(10);
        (Net(r, L(SystemLedger.CGSTInput)) + Net(r, L(SystemLedger.SGSTInput))).Should().Be(18 + 9);
        r.Stock.Single().QtyIn.Should().Be(12);
    }

    [Fact]
    public void Purchase_with_unclaimable_tax_books_tax_into_purchase()
    {
        var d = Calc(Doc(VoucherType.Purchase, lines: Line(1, 100)));
        var r = PostingEngine.PostTrade(d, new PostingContext { Ledger = L, InputTaxClaimable = false });
        Net(r, L(SystemLedger.Purchase)).Should().Be(118);
        Net(r, L(SystemLedger.CGSTInput)).Should().Be(0);
    }

    [Fact]
    public void Purchase_return_mirrors_purchase()
    {
        var d = Calc(Doc(VoucherType.PurchaseReturn, lines: Line(1, 100)));
        d.PaymentMode = PaymentMode.Credit; d.PartyLedgerId = Supplier;
        var r = PostingEngine.PostTrade(d, Ctx);
        Net(r, Supplier).Should().Be(118);
        Net(r, L(SystemLedger.PurchaseReturn)).Should().Be(-100);
        r.Stock.Single().QtyOut.Should().Be(1);
    }

    [Fact]
    public void Service_items_post_no_stock()
    {
        var d = Calc(Doc(lines: [Line(1, 100, cls: ItemClass.Service), Line(1, 50)]));
        var r = PostingEngine.PostTrade(d, Ctx);
        r.Stock.Should().ContainSingle();
    }

    [Fact]
    public void Orders_post_nothing()
    {
        var d = Calc(Doc(VoucherType.SalesOrder, lines: Line(1, 100)));
        var r = PostingEngine.PostTrade(d, Ctx);
        r.Legs.Should().BeEmpty();
        r.Stock.Should().BeEmpty();
    }

    [Fact]
    public void Delivery_note_posts_stock_only()
    {
        var d = Calc(Doc(VoucherType.DeliveryNote, lines: Line(3, 100)));
        var r = PostingEngine.PostTrade(d, Ctx);
        r.Legs.Should().BeEmpty();
        r.Stock.Single().QtyOut.Should().Be(3);
    }

    [Fact]
    public void Stock_uses_unit_factor()
    {
        var l = Line(2, 120, free: 1);
        l.UnitFactor = 12;
        var d = Calc(Doc(lines: l));
        PostingEngine.PostTrade(d, Ctx).Stock.Single().QtyOut.Should().Be(36);
    }

    [Fact]
    public void Adjustment_lines_need_direction()
    {
        var d = Calc(Doc(VoucherType.StockAdjustment, lines: Line(1, 10)));
        var act = () => PostingEngine.PostTrade(d, Ctx);
        act.Should().Throw<DomainException>().Where(e => e.Code == "direction_required");
        d.Lines[0].Direction = StockDirection.In;
        PostingEngine.PostTrade(d, Ctx).Stock.Single().QtyIn.Should().Be(1);
    }

    [Fact]
    public void Receipt_voucher_debits_cash_and_credits_lines()
    {
        var v = new Voucher { VoucherType = VoucherType.Receipt, HeaderLedgerId = L(SystemLedger.Cash), HeaderSide = EntrySide.Dr, DocumentNo = "R1" };
        v.Lines.Add(new VoucherLine { LineNo = 1, LedgerId = Customer, Amount = 500 });
        v.Lines.Add(new VoucherLine { LineNo = 2, LedgerId = Customer + 1, Amount = 250 });
        var r = PostingEngine.PostVoucher(v, Ctx);
        Net(r, L(SystemLedger.Cash)).Should().Be(750);
        Net(r, Customer).Should().Be(-500);
    }

    [Fact]
    public void Payment_voucher_credits_header()
    {
        var v = new Voucher { VoucherType = VoucherType.Payment, HeaderLedgerId = L(SystemLedger.Bank), HeaderSide = EntrySide.Cr, DocumentNo = "P1" };
        v.Lines.Add(new VoucherLine { LineNo = 1, LedgerId = Supplier, Amount = 900 });
        var r = PostingEngine.PostVoucher(v, Ctx);
        Net(r, L(SystemLedger.Bank)).Should().Be(-900);
        Net(r, Supplier).Should().Be(900);
    }

    [Fact]
    public void Journal_must_balance()
    {
        var v = new Voucher { VoucherType = VoucherType.Journal, DocumentNo = "J1" };
        v.Lines.Add(new VoucherLine { LineNo = 1, LedgerId = Customer, Side = EntrySide.Dr, Amount = 100 });
        v.Lines.Add(new VoucherLine { LineNo = 2, LedgerId = Supplier, Side = EntrySide.Cr, Amount = 90 });
        var act = () => PostingEngine.PostVoucher(v, Ctx);
        act.Should().Throw<DomainException>().Where(e => e.Code == "unbalanced");
        v.Lines[1].Amount = 100;
        PostingEngine.PostVoucher(v, Ctx).TotalDebit.Should().Be(100);
    }

    [Fact]
    public void Opening_voucher_posts_against_opening_balance_ledger()
    {
        var v = new Voucher { VoucherType = VoucherType.Opening, DocumentNo = "OB1" };
        v.Lines.Add(new VoucherLine { LineNo = 1, LedgerId = Customer, Side = EntrySide.Dr, Amount = 1000 });
        v.Lines.Add(new VoucherLine { LineNo = 2, LedgerId = Supplier, Side = EntrySide.Cr, Amount = 400 });
        var r = PostingEngine.PostVoucher(v, Ctx);
        Net(r, L(SystemLedger.OpeningBalance)).Should().Be(-600);
    }

    [Fact]
    public void Every_posting_permutation_balances()
    {
        foreach (var type in new[] { VoucherType.Sales, VoucherType.POSSales, VoucherType.SalesReturn, VoucherType.Purchase, VoucherType.PurchaseReturn })
        foreach (var tax in Enum.GetValues<TaxType>())
        foreach (var mode in new[] { RoundOffMode.Auto, RoundOffMode.Up, RoundOffMode.Down, RoundOffMode.None })
        {
            var d = Doc(type, tax, Line(3, 33.33m, gst: 18, cess: 1, addCess: 0.25m, kf: 1, disc: 5), Line(1, 99.99m, gst: 5, incl: true));
            d.RoundOffMode = mode; d.BillDiscount = 7.77m; d.Freight = 12.5m; d.FreightTaxRate = 18; d.OtherExpense = 3; d.CashDiscount = 1;
            d.PaymentMode = PaymentMode.Credit; d.PartyLedgerId = type.IsPurchaseSide() ? Supplier : Customer;
            Calc(d);
            var r = PostingEngine.PostTrade(d, Ctx);
            r.TotalDebit.Should().Be(r.TotalCredit, $"{type}/{tax}/{mode}");
            Net(r, d.PartyLedgerId!.Value).Should().Be(type is VoucherType.SalesReturn or VoucherType.Purchase ? -d.BillAmount : d.BillAmount, $"{type}/{tax}/{mode}");
        }
    }
}
