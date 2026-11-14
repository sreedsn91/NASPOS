using Kshethra.Domain;

namespace Kshethra.Application.Documents;

/// <summary>Business-rule warning the user may override (credit limit, negative stock) by resubmitting with AcknowledgeWarnings.</summary>
public class WarningException(IReadOnlyList<string> warnings)
    : DomainException(string.Join(" ", warnings), "confirm_required")
{
    public IReadOnlyList<string> Warnings { get; } = warnings;
}

public sealed class TradeLineDto
{
    public long Id { get; set; }
    public int LineNo { get; set; }
    public long ProductId { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public string? RegionalName { get; set; }
    public long? BatchId { get; set; }
    public string? BatchCode { get; set; }
    public StockDirection Direction { get; set; }
    public ItemClass ItemClass { get; set; }
    public string? HsnCode { get; set; }
    public string? Unit { get; set; }
    public decimal UnitFactor { get; set; } = 1;
    public bool IsAltUnit { get; set; }
    public decimal Qty { get; set; }
    public decimal FreeQty { get; set; }
    public decimal Mrp { get; set; }
    public decimal Rate { get; set; }
    /// <summary>Null = use the product's inclusive flag.</summary>
    public bool? TaxInclusive { get; set; }
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
    public decimal SaleRate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public decimal Cost { get; set; }
    public decimal Profit { get; set; }
    public decimal ProfitPercent { get; set; }
    public string? ExtraColumn { get; set; }
    public string? PatternNo { get; set; }
    public string? Remarks { get; set; }
}

public sealed class TradeDocumentDto
{
    public long Id { get; set; }
    public VoucherType VoucherType { get; set; }
    public string? DocumentNo { get; set; }
    public DateTimeOffset Date { get; set; }
    public int Version { get; set; }
    public string? RowVersion { get; set; }
    public bool AcknowledgeWarnings { get; set; }

    public long? PartyLedgerId { get; set; }
    public string? PartyName { get; set; }
    public string? PartyAddress { get; set; }
    public string? PartyPhone { get; set; }
    public string? PartyGstin { get; set; }
    public string? PartyStateCode { get; set; }
    public PaymentMode PaymentMode { get; set; } = PaymentMode.Cash;
    public TaxType? TaxType { get; set; }
    public BillType? BillType { get; set; }
    public long? SalespersonId { get; set; }
    public string? SalespersonName { get; set; }
    public long? PriceListId { get; set; }
    public string? Remarks { get; set; }
    public string? ReferenceNo { get; set; }
    public DateOnly? ReferenceDate { get; set; }
    public string? OrderDetails { get; set; }
    public string? DespatchDetails { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public long? SourceOrderId { get; set; }

    public decimal BillDiscountPercent { get; set; }
    public decimal BillDiscount { get; set; }
    public decimal OtherExpense { get; set; }
    public decimal OtherExpenseTaxRate { get; set; }
    public decimal Freight { get; set; }
    public decimal FreightTaxRate { get; set; }
    public RoundOffMode? RoundOffMode { get; set; }
    public decimal CashDiscountPercent { get; set; }
    public decimal CashDiscount { get; set; }
    public decimal CostFactor { get; set; } = 1;

    public decimal CashAmount { get; set; }
    public decimal CardAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal TenderedAmount { get; set; }
    public long? CardLedgerId { get; set; }
    public string? CardLast4 { get; set; }
    public string? PaymentReference { get; set; }
    public decimal PointsRedeemed { get; set; }
    public long? BomId { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }

    // Computed (output only)
    public decimal GrossAmount { get; set; }
    public decimal ItemDiscount { get; set; }
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
    public decimal OtherExpenseTax { get; set; }
    public decimal FreightTax { get; set; }
    public decimal RoundOff { get; set; }
    public decimal BillAmount { get; set; }
    public decimal TotalMrp { get; set; }
    public decimal Cost { get; set; }
    public decimal Profit { get; set; }
    public decimal PointsEarned { get; set; }
    public decimal RedeemAmount { get; set; }
    public bool Cancelled { get; set; }
    public string? CancelReason { get; set; }
    public bool IsProcessed { get; set; }
    public string? Counter { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];

    public List<TradeLineDto> Lines { get; set; } = [];
}

public sealed record DocumentListItem(long Id, VoucherType VoucherType, string DocumentNo, DateTimeOffset Date, string? PartyName,
    PaymentMode PaymentMode, decimal TaxableAmount, decimal TaxAmount, decimal BillAmount, bool Cancelled, bool IsProcessed,
    string? UserName, string? Counter, string? ReferenceNo);

public sealed record DocumentQuery(VoucherType? Type, DateOnly? From, DateOnly? To, long? PartyId, string? Search,
    bool? Cancelled, bool? OpenOrdersOnly, int Page = 1, int PageSize = 50);

public sealed record CancelRequest(string? Reason);

public sealed record VoidItemDto(string Screen, string? DocumentNo, long? ProductId, string ProductName, string? Batch, decimal Qty, decimal Rate, decimal Mrp);
