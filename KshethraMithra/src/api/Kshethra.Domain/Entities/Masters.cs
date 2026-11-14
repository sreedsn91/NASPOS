namespace Kshethra.Domain.Entities;

public class AccountGroup : TenantEntity
{
    public string Name { get; set; } = "";
    public AccountNature Nature { get; set; }
    /// <summary>Direct income/expense go to the trading section of the P&amp;L.</summary>
    public bool IsDirect { get; set; }
    public long? ParentId { get; set; }
    public AccountGroup? Parent { get; set; }
    public SystemGroup? SystemCode { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Every account (cash, bank, tax, income, expense, customer, supplier, employee) is a ledger.</summary>
public class Ledger : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PrintName { get; set; }
    public string? RegionalName { get; set; }
    public long AccountGroupId { get; set; }
    public AccountGroup? AccountGroup { get; set; }
    public PartyType PartyType { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? StateCode { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? Gstin { get; set; }
    public long? AreaId { get; set; }
    public Area? Area { get; set; }
    public long? PriceListId { get; set; }
    public PriceList? PriceList { get; set; }
    public long? LoyaltySchemeId { get; set; }
    public LoyaltyScheme? LoyaltyScheme { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal CreditLimit { get; set; }
    public int CreditDays { get; set; }
    public long? SalespersonId { get; set; }
    public bool SmsOptIn { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public SystemLedger? SystemCode { get; set; }
    /// <summary>Card/bank ledger usable as the card leg of a mixed payment.</summary>
    public bool IsCardLedger { get; set; }
    /// <summary>Corporate customer/supplier (company, firm, branch). Name uniqueness is enforced only for individuals.</summary>
    public bool IsCorporate { get; set; }
}

public class Area : TenantEntity
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Category : TenantEntity
{
    public string Name { get; set; } = "";
    public bool ShowInPos { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Color { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Manufacturer : TenantEntity
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class PriceList : TenantEntity
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class PriceListItem : Entity, ITenantEntity
{
    public long CompanyId { get; set; }
    public long PriceListId { get; set; }
    public long ProductId { get; set; }
    public decimal Price { get; set; }
}

public class Employee : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public long? LedgerId { get; set; }
    public Ledger? Ledger { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Gender { get; set; }
    public string? BloodGroup { get; set; }
    public TimeOnly? ShiftStart { get; set; }
    public TimeOnly? ShiftEnd { get; set; }
    public decimal Salary { get; set; }
    public decimal CommissionPercent { get; set; }
    public string? Category { get; set; }
    public bool IsSalesperson { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public class LoyaltyScheme : TenantEntity
{
    public string Name { get; set; } = "";
    /// <summary>Every <see cref="SaleAmount"/> rupees of bill value earns <see cref="Points"/> points.</summary>
    public decimal SaleAmount { get; set; }
    public decimal Points { get; set; }
    /// <summary>Balance a customer must reach before redeeming.</summary>
    public decimal TargetPoints { get; set; }
    /// <summary>Rupee value of one redeemed point.</summary>
    public decimal PointValue { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

public class Product : TenantEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RegionalName { get; set; }
    public string? HsnCode { get; set; }
    public long? CategoryId { get; set; }
    public Category? Category { get; set; }
    public long? ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public ItemClass ItemClass { get; set; } = ItemClass.Goods;

    public decimal CgstRate { get; set; }
    public decimal SgstRate { get; set; }
    public decimal IgstRate { get; set; }
    /// <summary>Second GST slab used when the unit sale rate exceeds <see cref="SecondaryTaxThreshold"/>.</summary>
    public bool HasSecondaryTax { get; set; }
    public decimal SecondaryTaxThreshold { get; set; }
    public decimal Cgst2Rate { get; set; }
    public decimal Sgst2Rate { get; set; }
    public decimal Igst2Rate { get; set; }
    public decimal CessRate { get; set; }
    public decimal AddCessPerUnit { get; set; }
    public decimal KfCessRate { get; set; }
    public bool SaleTaxInclusive { get; set; }
    public bool PurchaseTaxInclusive { get; set; }

    public decimal PurchaseRate { get; set; }
    public decimal SaleRate { get; set; }
    public decimal Mrp { get; set; }
    public decimal SaleMarginPercent { get; set; }
    public decimal MrpMarginPercent { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal CommissionPercent { get; set; }

    public string MainUnit { get; set; } = "NOS";
    public string? AltUnit { get; set; }
    /// <summary>Base units per alternate unit (e.g. 12 pieces per box).</summary>
    public decimal ConversionFactor { get; set; } = 1;
    public string? DefaultBarcode { get; set; }
    public decimal ReorderLevel { get; set; }
    public string? Rack { get; set; }
    public bool TrackExpiry { get; set; }
    public bool IsScaleItem { get; set; }
    public string? PluCode { get; set; }
    public decimal PiecesPerBox { get; set; }
    public decimal SqftPerBox { get; set; }
    public string? Remarks { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ProductImage> Images { get; set; } = [];

    // ---- temple offering (vazhipadu) ----
    /// <summary>Booked at the vazhipadu counter for a devotee and performed on a day (Archana, Payasam…).</summary>
    public bool IsOffering { get; set; }
    public long? DeityId { get; set; }
    public long? PoojaSlotId { get; set; }
    /// <summary>The counter asks for the devotee's name for this offering.</summary>
    public bool NeedsName { get; set; } = true;
    /// <summary>The counter asks for the devotee's birth star for this offering.</summary>
    public bool NeedsStar { get; set; } = true;
}

public class ProductImage : Entity
{
    public long ProductId { get; set; }
    public byte[] Content { get; set; } = [];
    public string ContentType { get; set; } = "image/png";
    public int SortOrder { get; set; }
}

/// <summary>Batch / barcode master with its rates. One row per product + batch code (+ MRP when multi-MRP is on).</summary>
public class Batch : TenantEntity
{
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public string Code { get; set; } = "";
    public long InternalNo { get; set; }
    public decimal PurchaseRate { get; set; }
    public decimal SaleRate { get; set; }
    public decimal Mrp { get; set; }
    /// <summary>Landed cost per base unit.</summary>
    public decimal Cost { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ItemOffer : TenantEntity
{
    public string Name { get; set; } = "";
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public long? BatchId { get; set; }
    public decimal Mrp { get; set; }
    public decimal Rate { get; set; }
    public decimal Discount { get; set; }
    public decimal NetRate { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Offer applies only inside its window (legacy compared the dates the wrong way round).</summary>
    public bool IsActiveAt(DateTimeOffset now) => IsActive && StartAt <= now && now <= EndAt;
}

public class Bom : TenantEntity
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? FinishedProductId { get; set; }
    public decimal BatchSize { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public List<BomLine> Lines { get; set; } = [];
}

public class BomLine : Entity
{
    public long BomId { get; set; }
    public int LineNo { get; set; }
    public long ProductId { get; set; }
    public Product? Product { get; set; }
    public BomLineKind Kind { get; set; }
    public decimal Qty { get; set; }
    public bool IsAltUnit { get; set; }
}
