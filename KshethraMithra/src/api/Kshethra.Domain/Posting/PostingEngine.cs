using Kshethra.Domain.Calculation;
using Kshethra.Domain.Entities;

namespace Kshethra.Domain.Posting;

public sealed record PostingLeg(long LedgerId, long? ContraLedgerId, decimal Debit, decimal Credit, string? Remarks);

public sealed record StockLeg(int LineNo, long ProductId, long BatchId, decimal QtyIn, decimal QtyOut,
    decimal Mrp, decimal SaleRate, decimal Cost);

public sealed class PostingResult
{
    public List<PostingLeg> Legs { get; } = [];
    public List<StockLeg> Stock { get; } = [];
    public decimal TotalDebit => Legs.Sum(l => l.Debit);
    public decimal TotalCredit => Legs.Sum(l => l.Credit);
}

public sealed class PostingContext
{
    public required Func<SystemLedger, long> Ledger { get; init; }
    /// <summary>When input tax can't be claimed it is booked into the purchase value instead of the input-tax ledgers.</summary>
    public bool InputTaxClaimable { get; init; } = true;
}

/// <summary>
/// Turns a calculated document into balanced double-entry legs and stock movements. Pure: no I/O, so every
/// rule is unit-testable. The infrastructure layer persists the result inside one database transaction.
/// </summary>
public static class PostingEngine
{
    public static PostingResult PostTrade(TradeDocument doc, PostingContext ctx)
    {
        var result = new PostingResult();
        var t = doc.VoucherType;
        if (t.PostsAccounts())
        {
            if (t.IsSalesFamily()) PostSale(doc, ctx, result, isReturn: false);
            else if (t == VoucherType.SalesReturn) PostSale(doc, ctx, result, isReturn: true);
            else if (t.IsPurchaseFamily()) PostPurchase(doc, ctx, result, isReturn: false);
            else if (t == VoucherType.PurchaseReturn) PostPurchase(doc, ctx, result, isReturn: true);
        }
        if (t.PostsStock()) PostStock(doc, result);
        AssertBalanced(result, doc.DocumentNo);
        return result;
    }

    public static PostingResult PostVoucher(Voucher v, PostingContext ctx)
    {
        var result = new PostingResult();
        var lines = v.Lines.Where(l => l.Amount != 0).OrderBy(l => l.LineNo).ToList();
        if (lines.Count == 0) throw new DomainException("A voucher needs at least one line with an amount.", "empty_voucher");
        if (lines.Any(l => l.Amount < 0)) throw new DomainException("Voucher amounts must be positive.", "negative_amount");

        if (v.VoucherType == VoucherType.Opening)
        {
            long ob = ctx.Ledger(SystemLedger.OpeningBalance);
            foreach (var l in lines)
            {
                if (l.Side == EntrySide.Dr) Pair(result, l.LedgerId, ob, l.Amount, l.Narration ?? v.Narration);
                else Pair(result, ob, l.LedgerId, l.Amount, l.Narration ?? v.Narration);
            }
        }
        else if (v.VoucherType == VoucherType.Journal || v.HeaderLedgerId is null)
        {
            decimal dr = lines.Where(l => l.Side == EntrySide.Dr).Sum(l => l.Amount);
            decimal cr = lines.Where(l => l.Side == EntrySide.Cr).Sum(l => l.Amount);
            if (dr != cr) throw new DomainException($"Journal is not balanced (Dr {dr:0.00} / Cr {cr:0.00}).", "unbalanced");
            long? topDr = lines.Where(l => l.Side == EntrySide.Dr).MaxBy(l => l.Amount)?.LedgerId;
            long? topCr = lines.Where(l => l.Side == EntrySide.Cr).MaxBy(l => l.Amount)?.LedgerId;
            foreach (var l in lines)
                result.Legs.Add(l.Side == EntrySide.Dr
                    ? new PostingLeg(l.LedgerId, topCr, l.Amount, 0, l.Narration ?? v.Narration)
                    : new PostingLeg(l.LedgerId, topDr, 0, l.Amount, l.Narration ?? v.Narration));
        }
        else
        {
            long header = v.HeaderLedgerId.Value;
            foreach (var l in lines)
            {
                if (l.LedgerId == header) throw new DomainException("A line cannot use the header ledger.", "same_ledger");
                if (v.HeaderSide == EntrySide.Dr) Pair(result, header, l.LedgerId, l.Amount, l.Narration ?? v.Narration);
                else Pair(result, l.LedgerId, header, l.Amount, l.Narration ?? v.Narration);
            }
        }
        AssertBalanced(result, v.DocumentNo);
        return result;
    }

    private static void PostSale(TradeDocument d, PostingContext ctx, PostingResult r, bool isReturn)
    {
        var L = ctx.Ledger;
        long cash = L(SystemLedger.Cash);
        long debtor = d.PaymentMode switch
        {
            PaymentMode.Cash => cash,
            PaymentMode.Credit => d.PartyLedgerId ?? throw new DomainException("A credit bill needs a customer.", "party_required"),
            _ => d.PartyLedgerId ?? cash,
        };

        // Sale: Dr debtor / Cr income & output tax. Return: the exact mirror.
        void Book(long ledger, decimal amount, string remark)
        {
            if (isReturn) Pair(r, ledger, debtor, amount, remark);
            else Pair(r, debtor, ledger, amount, remark);
        }

        decimal value = d.TaxableAmount + d.ExemptAmount;
        Book(L(isReturn ? SystemLedger.SalesReturn : SystemLedger.Sales), value, "Sale value");
        BookTaxes(d, L, output: true, Book);
        Book(L(SystemLedger.OtherExpense), d.OtherExpense, "Other charges");
        Book(L(SystemLedger.FreightCoolie), d.Freight, "Freight");
        Book(L(SystemLedger.RoundOff), d.RoundOff, "Round off");
        Book(L(SystemLedger.CashDiscount), -d.CashDiscount, "Cash discount");
        Book(L(SystemLedger.DiscountAllowed), -d.RedeemAmount, "Loyalty redeemed");

        if (!isReturn && d.PaymentMode == PaymentMode.Mixed)
        {
            decimal paid = d.CashAmount + d.CardAmount + d.CreditAmount;
            if (paid != d.BillAmount)
                throw new DomainException($"Mixed payment split ({paid:0.00}) must equal the bill amount ({d.BillAmount:0.00}).", "payment_split");
            if (d.CreditAmount > 0 && d.PartyLedgerId is null)
                throw new DomainException("The credit part of a mixed payment needs a customer.", "party_required");
            long card = d.CardLedgerId ?? L(SystemLedger.Bank);
            if (d.CardAmount > 0 && card != debtor) Pair(r, card, debtor, d.CardAmount, "Card / bank receipt");
            if (d.CashAmount > 0 && debtor != cash) Pair(r, cash, debtor, d.CashAmount, "Cash receipt");
        }
    }

    private static void PostPurchase(TradeDocument d, PostingContext ctx, PostingResult r, bool isReturn)
    {
        var L = ctx.Ledger;
        long cash = L(SystemLedger.Cash);
        long creditor = d.PaymentMode == PaymentMode.Cash
            ? cash
            : d.PartyLedgerId ?? throw new DomainException("A credit purchase needs a supplier.", "party_required");

        // Purchase: Dr expense & input tax / Cr creditor. Return: the exact mirror.
        void Book(long ledger, decimal amount, string remark)
        {
            if (isReturn) Pair(r, creditor, ledger, amount, remark);
            else Pair(r, ledger, creditor, amount, remark);
        }

        decimal value = d.TaxableAmount + d.ExemptAmount;
        long valueLedger = L(isReturn ? SystemLedger.PurchaseReturn : SystemLedger.Purchase);
        if (ctx.InputTaxClaimable)
        {
            Book(valueLedger, value, "Purchase value");
            BookTaxes(d, L, output: false, Book);
        }
        else
        {
            decimal allTax = d.CgstAmount + d.SgstAmount + d.IgstAmount + d.CessAmount + d.AddCessAmount
                             + d.KfCessAmount + d.OtherExpenseTax + d.FreightTax + d.ServiceTaxAmount + d.ServiceCessAmount;
            Book(valueLedger, value + allTax, "Purchase value (tax not claimable)");
        }
        Book(L(SystemLedger.OtherExpense), d.OtherExpense, "Other expense");
        Book(L(SystemLedger.FreightCoolie), d.Freight, "Freight");
        Book(L(SystemLedger.RoundOff), d.RoundOff, "Round off");
        Book(L(SystemLedger.DiscountReceived), -d.CashDiscount, "Cash discount");

        if (!isReturn && d.PaymentMode == PaymentMode.Mixed && d.PartyLedgerId is not null)
        {
            if (d.CashAmount + d.CardAmount > d.BillAmount)
                throw new DomainException("Amount paid exceeds the bill amount.", "payment_split");
            if (d.CashAmount > 0) Pair(r, creditor, cash, d.CashAmount, "Cash paid");
            if (d.CardAmount > 0) Pair(r, creditor, d.CardLedgerId ?? L(SystemLedger.Bank), d.CardAmount, "Bank paid");
        }
    }

    /// <summary>Books each tax to its output (sale) or input (purchase) ledger.</summary>
    private static void BookTaxes(TradeDocument d, Func<SystemLedger, long> L, bool output, Action<long, decimal, string> book)
    {
        decimal chargesTax = d.OtherExpenseTax + d.FreightTax;
        decimal cgst = d.CgstAmount, sgst = d.SgstAmount, igst = d.IgstAmount;
        if (chargesTax != 0)
        {
            if (d.TaxType == TaxType.InterState) igst += chargesTax;
            else
            {
                decimal half = DocumentCalculator.R2(chargesTax / 2);
                cgst += half;
                sgst += chargesTax - half;
            }
        }
        book(L(output ? SystemLedger.CGSTOutput : SystemLedger.CGSTInput), cgst, "CGST");
        book(L(output ? SystemLedger.SGSTOutput : SystemLedger.SGSTInput), sgst, "SGST");
        book(L(output ? SystemLedger.IGSTOutput : SystemLedger.IGSTInput), igst, "IGST");
        book(L(output ? SystemLedger.CessOutput : SystemLedger.CessInput), d.CessAmount + d.AddCessAmount, "Cess");
        book(L(output ? SystemLedger.KFCessOutput : SystemLedger.KFCessInput), d.KfCessAmount, "KF cess");
        book(L(SystemLedger.ServiceTax), d.ServiceTaxAmount, "Service tax");
        book(L(SystemLedger.ServiceCess), d.ServiceCessAmount, "Service cess");
    }

    private static void PostStock(TradeDocument d, PostingResult r)
    {
        foreach (var l in d.Lines.OrderBy(x => x.LineNo))
        {
            if (l.ItemClass == ItemClass.Service) continue;
            var dir = l.Direction != StockDirection.None ? l.Direction : d.VoucherType.DefaultDirection();
            if (dir == StockDirection.None)
                throw new DomainException($"Line {l.LineNo}: stock direction (in/out) is required.", "direction_required");
            if (l.BatchId is null or 0)
                throw new DomainException($"Line {l.LineNo}: batch is not resolved.", "batch_required");
            decimal qty = l.BaseQty;
            if (qty == 0) continue;
            decimal factor = l.UnitFactor == 0 ? 1 : l.UnitFactor;
            r.Stock.Add(new StockLeg(l.LineNo, l.ProductId, l.BatchId.Value,
                dir == StockDirection.In ? qty : 0, dir == StockDirection.Out ? qty : 0,
                DocumentCalculator.R4(l.Mrp / factor),
                DocumentCalculator.R4((d.VoucherType.IsPurchaseSide() ? l.SaleRate : l.Rate) / factor),
                l.Cost));
        }
    }

    /// <summary>Adds a balanced Dr/Cr pair; a negative amount swaps the sides. Zero is skipped.</summary>
    public static void Pair(PostingResult r, long dr, long cr, decimal amount, string? remark)
    {
        if (amount == 0) return;
        if (amount < 0) (dr, cr, amount) = (cr, dr, -amount);
        if (dr == cr) return;
        r.Legs.Add(new PostingLeg(dr, cr, amount, 0, remark));
        r.Legs.Add(new PostingLeg(cr, dr, 0, amount, remark));
    }

    public static void AssertBalanced(PostingResult r, string documentNo)
    {
        if (r.TotalDebit != r.TotalCredit)
            throw new DomainException($"Posting for {documentNo} is not balanced (Dr {r.TotalDebit:0.00} / Cr {r.TotalCredit:0.00}).", "unbalanced");
        if (r.Legs.Any(l => l.LedgerId <= 0))
            throw new DomainException($"Posting for {documentNo} references an unknown ledger.", "unknown_ledger");
    }
}
