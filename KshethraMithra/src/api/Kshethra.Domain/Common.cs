namespace Kshethra.Domain;

public abstract class Entity
{
    public long Id { get; set; }
}

/// <summary>Row belongs to one company (tenant). Filtered by a global EF query filter.</summary>
public interface ITenantEntity
{
    long CompanyId { get; set; }
}

public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    long? CreatedBy { get; set; }
    DateTimeOffset? UpdatedAt { get; set; }
    long? UpdatedBy { get; set; }
}

public abstract class TenantEntity : Entity, ITenantEntity, IAuditable
{
    public long CompanyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public long? UpdatedBy { get; set; }
    /// <summary>SQL Server rowversion for optimistic concurrency.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Thrown for business-rule violations; mapped to HTTP 400/409 by the API.</summary>
public class DomainException(string message, string code = "domain_error") : Exception(message)
{
    public string Code { get; } = code;
}

public static class VoucherTypeInfo
{
    public static bool IsSalesFamily(this VoucherType t) =>
        t is VoucherType.Sales or VoucherType.LocalSales or VoucherType.POSSales;

    public static bool IsPurchaseFamily(this VoucherType t) =>
        t is VoucherType.Purchase or VoucherType.LocalPurchase;

    public static bool IsOrder(this VoucherType t) =>
        t is VoucherType.Estimate or VoucherType.SalesOrder or VoucherType.PurchaseOrder;

    public static bool IsTradeDocument(this VoucherType t) => !t.IsAccountVoucher();

    public static bool IsAccountVoucher(this VoucherType t) =>
        t is VoucherType.Receipt or VoucherType.Payment or VoucherType.Journal or VoucherType.Contra
            or VoucherType.DebitNote or VoucherType.CreditNote or VoucherType.Opening;

    /// <summary>True when the document is priced at purchase rates (party = supplier).</summary>
    public static bool IsPurchaseSide(this VoucherType t) =>
        t is VoucherType.Purchase or VoucherType.LocalPurchase or VoucherType.PurchaseReturn
            or VoucherType.PurchaseOrder or VoucherType.ReceiptNote or VoucherType.OpeningStock;

    /// <summary>Documents whose line cost is the landed purchase cost (taxable + charges) rather than the batch cost.</summary>
    public static bool ComputesLandedCost(this VoucherType t) =>
        t is VoucherType.Purchase or VoucherType.LocalPurchase or VoucherType.ReceiptNote or VoucherType.OpeningStock
            or VoucherType.PurchaseOrder;

    /// <summary>Stock journals carry no GST.</summary>
    public static bool IsTaxFree(this VoucherType t) =>
        t is VoucherType.OpeningStock or VoucherType.StockAdjustment or VoucherType.Production;

    /// <summary>Default stock direction of a document line (production/adjustment set it per line).</summary>
    public static StockDirection DefaultDirection(this VoucherType t) => t switch
    {
        VoucherType.Sales or VoucherType.LocalSales or VoucherType.POSSales
            or VoucherType.DeliveryNote or VoucherType.PurchaseReturn => StockDirection.Out,
        VoucherType.Purchase or VoucherType.LocalPurchase or VoucherType.ReceiptNote
            or VoucherType.SalesReturn or VoucherType.OpeningStock => StockDirection.In,
        _ => StockDirection.None,
    };

    public static bool PostsAccounts(this VoucherType t) =>
        t.IsSalesFamily() || t.IsPurchaseFamily() || t is VoucherType.SalesReturn or VoucherType.PurchaseReturn;

    public static bool PostsStock(this VoucherType t) =>
        !t.IsOrder() && !t.IsAccountVoucher();

    public static string DisplayName(this VoucherType t) => t switch
    {
        VoucherType.LocalSales => "Local Sales",
        VoucherType.SalesOrder => "Sales Order",
        VoucherType.SalesReturn => "Sales Return",
        VoucherType.DeliveryNote => "Delivery Note",
        VoucherType.LocalPurchase => "Local Purchase",
        VoucherType.ReceiptNote => "Receipt Note",
        VoucherType.PurchaseReturn => "Purchase Return",
        VoucherType.DebitNote => "Debit Note",
        VoucherType.CreditNote => "Credit Note",
        VoucherType.OpeningStock => "Opening Stock",
        VoucherType.StockAdjustment => "Stock Adjustment",
        VoucherType.PurchaseOrder => "Purchase Order",
        VoucherType.POSSales => "POS Sales",
        VoucherType.Opening => "Opening Balance",
        _ => t.ToString(),
    };
}
