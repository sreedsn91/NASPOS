namespace Kshethra.Domain.Entities;

/// <summary>
/// One leg of a double-entry posting. A document's legs always sum Dr == Cr. Exactly one of
/// <see cref="TradeDocumentId"/> / <see cref="VoucherId"/> is set, both as real foreign keys.
/// </summary>
public class AccountEntry : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long FinancialYearId { get; set; }
    public VoucherType VoucherType { get; set; }
    public long? TradeDocumentId { get; set; }
    public long? VoucherId { get; set; }
    public string DocumentNo { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public long LedgerId { get; set; }
    public Ledger? Ledger { get; set; }
    /// <summary>The ledger on the other side of this leg (for day-book / statement display).</summary>
    public long? ContraLedgerId { get; set; }
    public Ledger? ContraLedger { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Remarks { get; set; }

    public long DocumentId => TradeDocumentId ?? VoucherId ?? 0;
}

/// <summary>Stock ledger: one row per inventory movement of a document line.</summary>
public class StockMovement : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long FinancialYearId { get; set; }
    public VoucherType VoucherType { get; set; }
    public long TradeDocumentId { get; set; }
    public string DocumentNo { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public long BatchId { get; set; }
    public Batch? Batch { get; set; }
    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }
    public decimal Mrp { get; set; }
    public decimal SaleRate { get; set; }
    /// <summary>Cost per base unit at the time of the movement.</summary>
    public decimal Cost { get; set; }
    public long? LedgerId { get; set; }
}

/// <summary>Bill-by-bill outstanding created by a credit sale/purchase (or an opening bill).</summary>
public class BillOutstanding : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long LedgerId { get; set; }
    public Ledger? Ledger { get; set; }
    public VoucherType VoucherType { get; set; }
    public long? TradeDocumentId { get; set; }
    public long? VoucherId { get; set; }
    public string DocumentNo { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Dr = receivable from the party, Cr = payable to the party.</summary>
    public EntrySide Side { get; set; }
    public decimal Amount { get; set; }
    public decimal Settled { get; set; }
    public bool Cancelled { get; set; }
    public List<BillSettlement> Settlements { get; set; } = [];
    public decimal Balance => Amount - Settled;
}

/// <summary>Allocation of a receipt/payment (voucher line) or a return document against an open bill.</summary>
public class BillSettlement : Entity
{
    public long BillId { get; set; }
    public BillOutstanding? Bill { get; set; }
    public long? VoucherLineId { get; set; }
    public long? TradeDocumentId { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset Date { get; set; }
}

public class LoyaltyTransaction : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long FinancialYearId { get; set; }
    public long LedgerId { get; set; }
    public long? SchemeId { get; set; }
    public VoucherType VoucherType { get; set; }
    /// <summary>The real document (legacy joined loyalty on a mismatched running number). Null for carried-forward balances.</summary>
    public long? TradeDocumentId { get; set; }
    public string? DocumentNo { get; set; }
    public DateTimeOffset Date { get; set; }
    public decimal SaleAmount { get; set; }
    public decimal PointsIn { get; set; }
    public decimal PointsOut { get; set; }
}
