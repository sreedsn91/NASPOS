using Kshethra.Domain.Entities;

namespace Kshethra.Domain.Calculation;

public sealed class CalcOptions
{
    /// <summary>Legacy service tax % applied to service-class lines (normally 0 under GST).</summary>
    public decimal ServiceTaxRate { get; init; }
    public decimal ServiceCessRate { get; init; }
    /// <summary>False when input tax can't be claimed: tax is then added to purchase cost.</summary>
    public bool TaxClaimable { get; init; } = true;
}

/// <summary>
/// The single GST calculation core. Fills every computed line and header field of a document from its
/// inputs (qty, rate, discounts, tax rates, charges, round-off mode). All money math is decimal; each tax
/// amount is rounded to 2 decimals per line and then summed.
/// </summary>
public static class DocumentCalculator
{
    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal R4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A tax-inclusive price is what the customer was promised, so the line must total exactly that. Rounding each tax
    /// separately can overshoot (₹60 at 18%: taxable 50.85 + CGST 4.58 + SGST 4.58 = 60.01). Instead the total tax is the
    /// inclusive amount less the taxable value, split across the percentage taxes by rate; the last one takes the paisa.
    /// With a discount the inclusive amount is rebuilt from the discounted taxable value.
    /// </summary>
    private static void FitInclusiveTax(TradeDocumentLine l)
    {
        decimal back = l.CgstRate + l.SgstRate + l.IgstRate + l.CessRate + l.KfCessRate;
        if (back <= 0) return;
        decimal target = l.DiscountAmount == 0 && l.BillDiscountShare == 0 ? R2(l.Qty * l.Rate) : R2(l.Taxable * (100m + back) / 100m);
        decimal totalTax = target - l.Taxable;
        var parts = new (decimal Rate, Action<decimal> Set)[]
        {
            (l.CgstRate, v => l.CgstAmount = v), (l.SgstRate, v => l.SgstAmount = v), (l.IgstRate, v => l.IgstAmount = v),
            (l.CessRate, v => l.CessAmount = v), (l.KfCessRate, v => l.KfCessAmount = v),
        };
        int last = Array.FindLastIndex(parts, p => p.Rate > 0);
        decimal given = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Rate <= 0) continue;
            decimal v = i == last ? totalTax - given : R2(totalTax * parts[i].Rate / back);
            parts[i].Set(v);
            given += v;
        }
    }

    public static void Calculate(TradeDocument doc, CalcOptions? options = null)
    {
        options ??= new CalcOptions();
        var lines = doc.Lines.OrderBy(l => l.LineNo).ToList();
        bool intra = doc.TaxType == TaxType.IntraState;
        bool inter = doc.TaxType == TaxType.InterState;

        // 1. Gross and item discount per line.
        foreach (var l in lines)
        {
            if (!intra) { l.CgstRate = 0; l.SgstRate = 0; }
            if (!inter) { l.IgstRate = 0; }
            if (doc.TaxType == TaxType.NoTax) { l.CessRate = 0; l.AddCessRate = 0; l.KfCessRate = 0; }

            decimal backOutRate = l.CgstRate + l.SgstRate + l.IgstRate + l.CessRate + l.KfCessRate;
            decimal value = l.Qty * l.Rate;
            l.Gross = l.TaxInclusive && backOutRate > 0 ? R2(value * 100m / (100m + backOutRate)) : R2(value);

            if (l.DiscountPercent > 0)
                l.DiscountAmount = R2(l.Gross * l.DiscountPercent / 100m);
            else if (l.DiscountAmount > 0 && l.Gross != 0)
                l.DiscountPercent = R4(l.DiscountAmount * 100m / l.Gross);
            if (l.DiscountAmount > l.Gross && l.Gross >= 0)
                throw new DomainException($"Line {l.LineNo}: discount exceeds the line amount.", "invalid_discount");
        }

        // 2. Bill discount, spread across lines by value (last line absorbs the rounding remainder).
        decimal sumAmount = lines.Sum(l => l.Gross - l.DiscountAmount);
        if (doc.BillDiscountPercent > 0)
            doc.BillDiscount = R2(sumAmount * doc.BillDiscountPercent / 100m);
        if (doc.BillDiscount > sumAmount && sumAmount >= 0)
            throw new DomainException("Bill discount exceeds the bill value.", "invalid_discount");
        decimal remaining = doc.BillDiscount;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            decimal amount = l.Gross - l.DiscountAmount;
            l.BillDiscountShare = i == lines.Count - 1
                ? remaining
                : sumAmount == 0 ? 0 : R2(doc.BillDiscount * amount / sumAmount);
            remaining -= l.BillDiscountShare;
        }

        // 3. Taxes per line.
        foreach (var l in lines)
        {
            l.Taxable = l.Gross - l.DiscountAmount - l.BillDiscountShare;
            l.CgstAmount = R2(l.Taxable * l.CgstRate / 100m);
            l.SgstAmount = R2(l.Taxable * l.SgstRate / 100m);
            l.IgstAmount = R2(l.Taxable * l.IgstRate / 100m);
            l.CessAmount = R2(l.Taxable * l.CessRate / 100m);
            l.AddCessAmount = R2(l.Qty * l.AddCessRate);
            l.KfCessAmount = R2(l.Taxable * l.KfCessRate / 100m);
            if (l.TaxInclusive) FitInclusiveTax(l);
            l.LineTotal = l.Taxable + l.CgstAmount + l.SgstAmount + l.IgstAmount + l.CessAmount + l.AddCessAmount + l.KfCessAmount;
        }

        // 4. Header totals.
        doc.GrossAmount = lines.Sum(l => l.Gross);
        doc.ItemDiscount = lines.Sum(l => l.DiscountAmount);
        doc.TaxableAmount = lines.Where(l => l.TaxRateTotal > 0).Sum(l => l.Taxable);
        doc.ExemptAmount = lines.Where(l => l.TaxRateTotal == 0).Sum(l => l.Taxable);
        doc.CgstAmount = lines.Sum(l => l.CgstAmount);
        doc.SgstAmount = lines.Sum(l => l.SgstAmount);
        doc.IgstAmount = lines.Sum(l => l.IgstAmount);
        doc.CessAmount = lines.Sum(l => l.CessAmount);
        doc.AddCessAmount = lines.Sum(l => l.AddCessAmount);
        doc.KfCessAmount = lines.Sum(l => l.KfCessAmount);
        doc.TotalMrp = R2(lines.Sum(l => l.Qty * l.Mrp));

        decimal serviceTaxable = lines.Where(l => l.ItemClass == ItemClass.Service).Sum(l => l.Taxable);
        doc.ServiceTaxAmount = R2(serviceTaxable * options.ServiceTaxRate / 100m);
        doc.ServiceCessAmount = R2(doc.ServiceTaxAmount * options.ServiceCessRate / 100m);
        doc.OtherExpenseTax = doc.TaxType == TaxType.NoTax ? 0 : R2(doc.OtherExpense * doc.OtherExpenseTaxRate / 100m);
        doc.FreightTax = doc.TaxType == TaxType.NoTax ? 0 : R2(doc.Freight * doc.FreightTaxRate / 100m);

        decimal preRound = lines.Sum(l => l.LineTotal) + doc.OtherExpense + doc.OtherExpenseTax
                           + doc.Freight + doc.FreightTax + doc.ServiceTaxAmount + doc.ServiceCessAmount;
        decimal rounded = RoundOff(preRound, doc.RoundOffMode);
        doc.RoundOff = rounded - preRound;

        if (doc.CashDiscountPercent > 0)
            doc.CashDiscount = R2(rounded * doc.CashDiscountPercent / 100m);
        doc.BillAmount = rounded - doc.CashDiscount - doc.RedeemAmount;
        if (doc.BillAmount < 0)
            throw new DomainException("Discounts exceed the bill amount.", "invalid_discount");

        // 5. Cost and profit.
        if (doc.VoucherType.ComputesLandedCost())
            CalculatePurchaseCost(doc, lines, options);
        else
            foreach (var l in lines)
            {
                l.Profit = R2(l.Taxable - l.Cost * l.BaseQty);
                decimal costTotal = l.Cost * l.BaseQty;
                l.ProfitPercent = costTotal == 0 ? 0 : R2(l.Profit * 100m / costTotal);
            }

        doc.Cost = R2(lines.Sum(l => l.Cost * l.BaseQty));
        doc.Profit = doc.VoucherType.IsPurchaseSide() ? 0 : R2(lines.Sum(l => l.Taxable) - doc.Cost - doc.CashDiscount);
    }

    /// <summary>
    /// Landed cost per base unit: taxable value plus its share of freight / other expense (and tax when not
    /// claimable), times the cost factor, divided by the base quantity received (paid + free).
    /// </summary>
    private static void CalculatePurchaseCost(TradeDocument doc, List<TradeDocumentLine> lines, CalcOptions options)
    {
        decimal sumTaxable = lines.Sum(l => l.Taxable);
        decimal charges = doc.OtherExpense + doc.Freight;
        decimal factor = doc.CostFactor <= 0 ? 1 : doc.CostFactor;
        foreach (var l in lines)
        {
            decimal baseQty = l.BaseQty;
            if (baseQty == 0) { l.Cost = 0; continue; }
            decimal value = l.Taxable + (sumTaxable == 0 ? 0 : charges * l.Taxable / sumTaxable);
            if (!options.TaxClaimable)
                value += l.CgstAmount + l.SgstAmount + l.IgstAmount + l.CessAmount + l.AddCessAmount + l.KfCessAmount;
            l.Cost = R4(value * factor / baseQty);
            decimal mrpPerBase = l.UnitFactor == 0 ? 0 : l.Mrp / l.UnitFactor;
            l.Profit = R2((mrpPerBase - l.Cost) * baseQty);
            l.ProfitPercent = l.Cost == 0 ? 0 : R2((mrpPerBase - l.Cost) * 100m / l.Cost);
        }
    }

    /// <summary>
    /// Auto: nearest rupee (.50 goes up). Up: next .50 or rupee. Down: previous .50 or rupee. None: 2 decimals.
    /// Negative amounts are rounded symmetrically.
    /// </summary>
    public static decimal RoundOff(decimal amount, RoundOffMode mode)
    {
        if (amount < 0) return -RoundOff(-amount, mode);
        decimal whole = Math.Truncate(amount);
        decimal frac = R2(amount - whole);
        return mode switch
        {
            RoundOffMode.Auto => frac >= 0.50m ? whole + 1 : whole,
            RoundOffMode.Up => frac == 0 ? whole : frac <= 0.50m ? whole + 0.50m : whole + 1,
            RoundOffMode.Down => frac >= 0.50m ? whole + 0.50m : whole,
            _ => R2(amount),
        };
    }

    /// <summary>
    /// Production: raw-material cost (at batch cost) plus labour and other cost is split equally across the finished
    /// lines actually present (blank rows ignored — the legacy code divided by rowCount − 1).
    /// </summary>
    public static void SpreadProductionCost(TradeDocument doc)
    {
        var raw = doc.Lines.Where(l => l.Direction == StockDirection.Out && l.BaseQty > 0).ToList();
        var finished = doc.Lines.Where(l => l.Direction == StockDirection.In && l.BaseQty > 0).ToList();
        if (finished.Count == 0) throw new DomainException("Production needs at least one finished item.", "no_finished");
        if (raw.Count == 0) throw new DomainException("Production needs at least one raw material.", "no_raw");
        decimal total = raw.Sum(l => l.Cost * l.BaseQty) + doc.LabourCost + doc.OtherCost;
        decimal share = total / finished.Count;
        foreach (var f in finished)
        {
            f.Cost = R4(share / f.BaseQty);
            f.Rate = R4(f.Cost * f.UnitFactor);
        }
        doc.Cost = R2(total);
    }

    /// <summary>Loyalty points earned on a bill value: billAmount / saleAmount × points.</summary>
    public static decimal LoyaltyPoints(decimal billAmount, LoyaltyScheme? scheme)
    {
        if (scheme is null || scheme.SaleAmount <= 0 || billAmount <= 0) return 0;
        return R2(billAmount / scheme.SaleAmount * scheme.Points);
    }
}
