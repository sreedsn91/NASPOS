namespace Kshethra.Domain.Entities;

/// <summary>
/// Header of every inventory document: sales, POS, returns, orders, estimates, delivery/receipt notes,
/// purchases, opening stock, stock adjustment and production. <see cref="VoucherType"/> discriminates.
/// </summary>
public class TradeDocument : TenantEntity
{
    public long FinancialYearId { get; set; }
    public VoucherType VoucherType { get; set; }
    public string DocumentNo { get; set; } = "";
    public long SequenceNo { get; set; }
    public DateTimeOffset Date { get; set; }
    /// <summary>Incremented each time the document is edited (reverse + repost).</summary>
    public int Version { get; set; } = 1;

    public long? PartyLedgerId { get; set; }
    public Ledger? PartyLedger { get; set; }
    public string? PartyName { get; set; }
    public string? PartyAddress { get; set; }
    public string? PartyPhone { get; set; }
    public string? PartyGstin { get; set; }
    public string? PartyStateCode { get; set; }

    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;
    public TaxType TaxType { get; set; } = TaxType.IntraState;
    public BillType BillType { get; set; } = BillType.B2C;
    public long? SalespersonId { get; set; }
    public Employee? Salesperson { get; set; }
    public long? PriceListId { get; set; }
    public string? Remarks { get; set; }
    /// <summary>Supplier's invoice number on purchases.</summary>
    public string? ReferenceNo { get; set; }
    public DateOnly? ReferenceDate { get; set; }
    public string? OrderDetails { get; set; }
    public string? DespatchDetails { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public long? SourceOrderId { get; set; }
    /// <summary>Orders/estimates: set once converted to a bill.</summary>
    public bool IsProcessed { get; set; }

    public bool Cancelled { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public long? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    public decimal GrossAmount { get; set; }
    public decimal ItemDiscount { get; set; }
    public decimal BillDiscountPercent { get; set; }
    public decimal BillDiscount { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal ExemptAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal CessAmount { get; set; }
    public decimal AddCessAmount { get; set; }
    public decimal KfCessAmount { get; set; }
    public decimal ServiceTaxAmount { get; set; }
    public decimal ServiceCessAmount { get; set; }
    public decimal OtherExpense { get; set; }
    public decimal OtherExpenseTaxRate { get; set; }
    public decimal OtherExpenseTax { get; set; }
    public decimal Freight { get; set; }
    public decimal FreightTaxRate { get; set; }
    public decimal FreightTax { get; set; }
    public RoundOffMode RoundOffMode { get; set; }
    public decimal RoundOff { get; set; }
    public decimal CashDiscountPercent { get; set; }
    public decimal CashDiscount { get; set; }
    public decimal BillAmount { get; set; }
    public decimal TotalMrp { get; set; }
    public decimal Cost { get; set; }
    public decimal Profit { get; set; }
    public decimal CostFactor { get; set; } = 1;

    public decimal CashAmount { get; set; }
    public decimal CardAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal TenderedAmount { get; set; }
    public long? CardLedgerId { get; set; }
    /// <summary>Masked card number (last four digits only). Full PAN/CVV are never stored.</summary>
    public string? CardLast4 { get; set; }
    public string? PaymentReference { get; set; }

    public long? LoyaltySchemeId { get; set; }
    public decimal PointsEarned { get; set; }
    public decimal PointsRedeemed { get; set; }
    public decimal RedeemAmount { get; set; }

    public long? BomId { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }

    public long? UserId { get; set; }
    public string? Counter { get; set; }

    public List<TradeDocumentLine> Lines { get; set; } = [];
}

public class TradeDocumentLine : Entity
{
    public long DocumentId { get; set; }
    public int LineNo { get; set; }
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public long? BatchId { get; set; }
    public Batch? Batch { get; set; }
    public string? BatchCode { get; set; }
    /// <summary>Explicit direction for production / stock adjustment lines; otherwise from the voucher type.</summary>
    public StockDirection Direction { get; set; }
    public ItemClass ItemClass { get; set; } = ItemClass.Goods;
    public string? HsnCode { get; set; }
    public string? Unit { get; set; }
    public decimal UnitFactor { get; set; } = 1;
    public bool IsAltUnit { get; set; }
    public decimal Qty { get; set; }
    public decimal FreeQty { get; set; }
    public decimal Mrp { get; set; }
    public decimal Rate { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal Gross { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal BillDiscountShare { get; set; }
    public decimal Taxable { get; set; }
    public decimal CgstRate { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstRate { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal IgstRate { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal CessRate { get; set; }
    public decimal CessAmount { get; set; }
    public decimal AddCessRate { get; set; }
    public decimal AddCessAmount { get; set; }
    public decimal KfCessRate { get; set; }
    public decimal KfCessAmount { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Purchase lines: selling rate to set on the batch.</summary>
    public decimal SaleRate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    /// <summary>Cost per base unit.</summary>
    public decimal Cost { get; set; }
    public decimal Profit { get; set; }
    public decimal ProfitPercent { get; set; }
    public string? ExtraColumn { get; set; }
    public string? PatternNo { get; set; }
    public string? Remarks { get; set; }

    public decimal TaxRateTotal => CgstRate + SgstRate + IgstRate;
    public decimal BaseQty => (Qty + FreeQty) * UnitFactor;
}

public class Voucher : TenantEntity
{
    public long FinancialYearId { get; set; }
    public VoucherType VoucherType { get; set; }
    public string DocumentNo { get; set; } = "";
    public long SequenceNo { get; set; }
    public DateTimeOffset Date { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>Cash/bank (or other) ledger in the header. Null for journals, whose lines carry their own side.</summary>
    public long? HeaderLedgerId { get; set; }
    public Ledger? HeaderLedger { get; set; }
    public EntrySide HeaderSide { get; set; }
    public decimal Total { get; set; }
    public string? Narration { get; set; }
    public string? ReferenceNo { get; set; }
    public string? ChequeNo { get; set; }
    public DateOnly? ChequeDate { get; set; }
    public string? Bank { get; set; }
    public ChequeStatus ChequeStatus { get; set; }
    public bool IsReconciled { get; set; }
    public DateOnly? ReconciledOn { get; set; }
    public long? SalespersonId { get; set; }
    public bool Cancelled { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public long? CancelledBy { get; set; }
    public long? UserId { get; set; }
    public string? Counter { get; set; }
    public List<VoucherLine> Lines { get; set; } = [];
}

public class VoucherLine : Entity
{
    public long VoucherId { get; set; }
    public int LineNo { get; set; }
    public long LedgerId { get; set; }
    public Ledger? Ledger { get; set; }
    public EntrySide Side { get; set; }
    public decimal Amount { get; set; }
    public string? Narration { get; set; }
    public List<BillSettlement> Settlements { get; set; } = [];
}
