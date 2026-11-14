namespace Kshethra.Domain;

public enum VoucherType
{
    Sales = 1,
    LocalSales = 2,
    Estimate = 3,
    SalesOrder = 4,
    SalesReturn = 5,
    DeliveryNote = 6,
    Purchase = 7,
    LocalPurchase = 8,
    ReceiptNote = 9,
    PurchaseReturn = 10,
    Receipt = 11,
    Payment = 12,
    Journal = 13,
    Contra = 14,
    DebitNote = 15,
    CreditNote = 16,
    OpeningStock = 17,
    StockAdjustment = 18,
    Production = 19,
    PurchaseOrder = 20,
    POSSales = 21,
    Opening = 22,
}

/// <summary>Stable identifiers for the ledgers the posting engine relies on.</summary>
public enum SystemLedger
{
    OpeningBalance = 1,
    Sales,
    Purchase,
    Cash,
    Bank,
    SalesReturn,
    PurchaseReturn,
    DiscountReceived,
    DiscountAllowed,
    OtherIncome,
    OtherExpense,
    ProfitLoss,
    RoundOff,
    CashDiscount,
    FreightCoolie,
    CGSTOutput,
    CGSTInput,
    SGSTOutput,
    SGSTInput,
    IGSTOutput,
    IGSTInput,
    CessOutput,
    CessInput,
    KFCessOutput,
    KFCessInput,
    ServiceTax,
    ServiceCess,
    WalkInCustomer,
}

public enum AccountNature { Asset = 1, Liability = 2, Income = 3, Expense = 4 }

/// <summary>Well-known account groups seeded for every company.</summary>
public enum SystemGroup
{
    CurrentAssets = 1,
    FixedAssets,
    CurrentLiabilities,
    LongTermLiabilities,
    Capital,
    DirectIncome,
    IndirectIncome,
    DirectExpense,
    IndirectExpense,
    SundryDebtors,
    SundryCreditors,
    SalesAccounts,
    PurchaseAccounts,
    DutiesAndTaxes,
    BankAccounts,
    CashInHand,
    Employees,
    Loans,
}

public enum PartyType { None = 0, Customer = 1, Supplier = 2, Employee = 3 }

public enum PaymentMode { Cash = 1, Credit = 2, Mixed = 3 }

public enum TaxType { IntraState = 1, InterState = 2, NoTax = 3 }

public enum BillType { B2C = 1, B2B = 2 }

public enum ItemClass { Goods = 1, Service = 2 }

public enum RoundOffMode { Auto = 0, Up = 1, Down = 2, None = 3 }

public enum NegativeStockMode { Allow = 0, Warn = 1, Block = 2 }

public enum CreditLimitMode { Ignore = 0, Warn = 1, Block = 2 }

public enum EntrySide { Dr = 1, Cr = 2 }

public enum StockDirection { Out = -1, None = 0, In = 1 }

public enum BomLineKind { RawMaterial = 1, FinishedGood = 2 }

public enum ChequeStatus { None = 0, Pending = 1, Cleared = 2, Bounced = 3 }

public enum PrintFormat { Thermal80 = 1, Thermal58 = 2, A5 = 3, A4 = 4 }

public enum MessageChannel { Sms = 1, Email = 2 }

public enum MessageStatus { Queued = 1, Sent = 2, Failed = 3, Cancelled = 4 }

[Flags]
public enum Right
{
    None = 0,
    Read = 1,
    Add = 2,
    Edit = 4,
    Cancel = 8,
    Delete = 16,
    Print = 32,
    Export = 64,
    Email = 128,
    Sms = 256,
    All = Read | Add | Edit | Cancel | Delete | Print | Export | Email | Sms,
}
