using FluentAssertions;
using NasPos.Domain;
using NasPos.Domain.Calculation;
using NasPos.Domain.Entities;

namespace NasPos.Tests.Domain;

public class CalculatorTests
{
    internal static TradeDocument Doc(VoucherType type = VoucherType.Sales, TaxType tax = TaxType.IntraState, params TradeDocumentLine[] lines)
    {
        var d = new TradeDocument { VoucherType = type, TaxType = tax, RoundOffMode = RoundOffMode.None, DocumentNo = "T1" };
        int n = 1;
        foreach (var l in lines) { l.LineNo = n++; d.Lines.Add(l); }
        return d;
    }

    internal static TradeDocumentLine Line(decimal qty, decimal rate, decimal gst = 18, bool incl = false, decimal cess = 0,
        decimal addCess = 0, decimal kf = 0, decimal disc = 0, decimal free = 0, decimal cost = 0, decimal mrp = 0,
        ItemClass cls = ItemClass.Goods, long batch = 1, long product = 1) => new()
    {
        Qty = qty, Rate = rate, TaxInclusive = incl, CgstRate = gst / 2, SgstRate = gst / 2, IgstRate = gst,
        CessRate = cess, AddCessRate = addCess, KfCessRate = kf, DiscountPercent = disc, FreeQty = free,
        Cost = cost, Mrp = mrp, UnitFactor = 1, ItemClass = cls, BatchId = batch, ProductId = product,
    };

    [Fact]
    public void Exclusive_intra_state_splits_cgst_sgst()
    {
        var d = Doc(lines: Line(2, 100));
        DocumentCalculator.Calculate(d);
        var l = d.Lines[0];
        l.Gross.Should().Be(200);
        l.CgstAmount.Should().Be(18);
        l.SgstAmount.Should().Be(18);
        l.IgstAmount.Should().Be(0);
        l.IgstRate.Should().Be(0);
        d.BillAmount.Should().Be(236);
    }

    [Fact]
    public void Inter_state_uses_igst_only()
    {
        var d = Doc(tax: TaxType.InterState, lines: Line(1, 100, gst: 12));
        DocumentCalculator.Calculate(d);
        d.IgstAmount.Should().Be(12);
        d.CgstAmount.Should().Be(0);
        d.SgstAmount.Should().Be(0);
        d.BillAmount.Should().Be(112);
    }

    [Fact]
    public void Inclusive_price_backs_tax_out()
    {
        var d = Doc(lines: Line(1, 118, incl: true));
        DocumentCalculator.Calculate(d);
        d.Lines[0].Gross.Should().Be(100);
        d.Lines[0].LineTotal.Should().Be(118);
    }

    [Fact]
    public void Inclusive_price_with_cess_backs_all_rates_out()
    {
        var d = Doc(lines: Line(1, 1280, gst: 28, incl: true, cess: 0)); // 28% slab
        DocumentCalculator.Calculate(d);
        d.Lines[0].Gross.Should().Be(1000);
        var e = Doc(lines: Line(1, 1300, gst: 28, incl: true, cess: 2));
        DocumentCalculator.Calculate(e);
        e.Lines[0].Gross.Should().Be(1000);
        e.Lines[0].CessAmount.Should().Be(20);
        e.Lines[0].LineTotal.Should().Be(1300);
    }

    [Fact]
    public void Tax_rounded_per_line_then_summed()
    {
        // 3 lines of 10.10 @ 5%: per-line tax 0.2525 -> cgst 0.25+sgst 0.25 each
        var d = Doc(lines: [Line(1, 10.10m, gst: 5), Line(1, 10.10m, gst: 5), Line(1, 10.10m, gst: 5)]);
        DocumentCalculator.Calculate(d);
        d.Lines.Should().AllSatisfy(l => l.CgstAmount.Should().Be(0.25m));
        d.CgstAmount.Should().Be(0.75m);
        d.SgstAmount.Should().Be(0.75m);
    }

    [Fact]
    public void Item_discount_and_bill_discount_share_reduce_taxable()
    {
        var d = Doc(lines: [Line(1, 100, disc: 10), Line(1, 200)]);
        d.BillDiscount = 29; // spread 90:200
        DocumentCalculator.Calculate(d);
        d.Lines[0].DiscountAmount.Should().Be(10);
        d.Lines[0].BillDiscountShare.Should().Be(9);
        d.Lines[1].BillDiscountShare.Should().Be(20);
        d.Lines.Sum(l => l.BillDiscountShare).Should().Be(29);
        d.Lines[0].Taxable.Should().Be(81);
        d.Lines[1].Taxable.Should().Be(180);
    }

    [Fact]
    public void Bill_discount_remainder_goes_to_last_line()
    {
        var d = Doc(lines: [Line(1, 100), Line(1, 100), Line(1, 100)]);
        d.BillDiscount = 10;
        DocumentCalculator.Calculate(d);
        d.Lines.Sum(l => l.BillDiscountShare).Should().Be(10);
        d.Lines[2].BillDiscountShare.Should().Be(3.34m);
    }

    [Fact]
    public void Cess_add_cess_and_kf_cess()
    {
        var d = Doc(lines: Line(10, 10, gst: 12, cess: 1, addCess: 0.5m, kf: 1));
        DocumentCalculator.Calculate(d);
        var l = d.Lines[0];
        l.CessAmount.Should().Be(1);
        l.AddCessAmount.Should().Be(5);
        l.KfCessAmount.Should().Be(1);
        l.LineTotal.Should().Be(100 + 12 + 1 + 5 + 1);
    }

    [Fact]
    public void Exempt_lines_are_bucketed_separately()
    {
        var d = Doc(lines: [Line(1, 50, gst: 0), Line(1, 100, gst: 5)]);
        DocumentCalculator.Calculate(d);
        d.ExemptAmount.Should().Be(50);
        d.TaxableAmount.Should().Be(100);
    }

    [Theory]
    [InlineData(RoundOffMode.Auto, 100.49, 100.00)]
    [InlineData(RoundOffMode.Auto, 100.50, 101.00)]
    [InlineData(RoundOffMode.Up, 100.10, 100.50)]
    [InlineData(RoundOffMode.Up, 100.60, 101.00)]
    [InlineData(RoundOffMode.Up, 100.00, 100.00)]
    [InlineData(RoundOffMode.Down, 100.60, 100.50)]
    [InlineData(RoundOffMode.Down, 100.40, 100.00)]
    [InlineData(RoundOffMode.None, 100.456, 100.46)]
    [InlineData(RoundOffMode.Auto, -100.50, -101.00)]
    public void Round_off_modes(RoundOffMode mode, double amount, double expected) =>
        DocumentCalculator.RoundOff((decimal)amount, mode).Should().Be((decimal)expected);

    [Fact]
    public void Round_off_and_cash_discount_in_bill_amount()
    {
        var d = Doc(lines: Line(1, 99.70m, gst: 0));
        d.RoundOffMode = RoundOffMode.Auto;
        d.CashDiscount = 5;
        DocumentCalculator.Calculate(d);
        d.RoundOff.Should().Be(0.30m);
        d.BillAmount.Should().Be(95);
    }

    [Fact]
    public void Service_tax_applies_to_service_lines_only()
    {
        var d = Doc(lines: [Line(1, 100, gst: 0, cls: ItemClass.Service), Line(1, 100, gst: 0)]);
        DocumentCalculator.Calculate(d, new CalcOptions { ServiceTaxRate = 10, ServiceCessRate = 2 });
        d.ServiceTaxAmount.Should().Be(10);
        d.ServiceCessAmount.Should().Be(0.20m);
        d.BillAmount.Should().Be(210.20m);
    }

    [Fact]
    public void Sale_profit_uses_base_qty_including_free()
    {
        var d = Doc(lines: Line(2, 100, gst: 0, free: 1, cost: 50));
        DocumentCalculator.Calculate(d);
        d.Lines[0].Profit.Should().Be(200 - 150);
        d.Profit.Should().Be(50);
    }

    [Fact]
    public void Purchase_landed_cost_spreads_charges_and_free_qty()
    {
        var d = Doc(VoucherType.Purchase, lines: [Line(10, 10, free: 0, mrp: 20), Line(5, 20, mrp: 30)]);
        d.Freight = 20; // spread 100:100
        DocumentCalculator.Calculate(d);
        d.Lines[0].Cost.Should().Be(11);
        d.Lines[1].Cost.Should().Be(22);
        d.Lines[0].ProfitPercent.Should().Be(81.82m);
    }

    [Fact]
    public void Purchase_cost_includes_tax_when_not_claimable()
    {
        var d = Doc(VoucherType.Purchase, lines: Line(10, 10, gst: 18));
        DocumentCalculator.Calculate(d, new CalcOptions { TaxClaimable = false });
        d.Lines[0].Cost.Should().Be(11.8m);
    }

    [Fact]
    public void Freight_gst_in_bill_total()
    {
        var d = Doc(VoucherType.Purchase, lines: Line(1, 100, gst: 0));
        d.Freight = 100; d.FreightTaxRate = 18;
        DocumentCalculator.Calculate(d);
        d.FreightTax.Should().Be(18);
        d.BillAmount.Should().Be(218);
    }

    [Fact]
    public void Discount_exceeding_gross_is_rejected()
    {
        var l = Line(1, 100);
        l.DiscountAmount = 150;
        var act = () => DocumentCalculator.Calculate(Doc(lines: l));
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Production_cost_is_split_across_actual_finished_lines()
    {
        var raw = Line(10, 0, gst: 0, cost: 5);
        raw.Direction = StockDirection.Out;
        var f1 = Line(4, 0, gst: 0); f1.Direction = StockDirection.In;
        var f2 = Line(2, 0, gst: 0); f2.Direction = StockDirection.In;
        var blank = Line(0, 0, gst: 0); blank.Direction = StockDirection.In;
        var d = Doc(VoucherType.Production, TaxType.NoTax, raw, f1, f2, blank);
        d.LabourCost = 10;
        DocumentCalculator.SpreadProductionCost(d);
        // (10 × 5 + 10) = 60 split over the 2 real finished lines = 30 each.
        f1.Cost.Should().Be(7.5m);
        f2.Cost.Should().Be(15m);
        d.Cost.Should().Be(60);
    }

    [Fact]
    public void Loyalty_points_are_proportional()
    {
        var scheme = new LoyaltyScheme { SaleAmount = 100, Points = 1 };
        DocumentCalculator.LoyaltyPoints(250, scheme).Should().Be(2.5m);
        DocumentCalculator.LoyaltyPoints(250, null).Should().Be(0);
    }
}
