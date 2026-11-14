using Kshethra.Domain;

namespace Kshethra.Application.Common;

/// <summary>Screen names used for role permissions. Every endpoint declares the screen + right it needs.</summary>
public static class Screens
{
    public const string Dashboard = "Dashboard";
    public const string Company = "Company";
    public const string Settings = "Settings";
    public const string Users = "Users";
    public const string Roles = "Roles";
    public const string UserLog = "UserLog";
    public const string AccountGroup = "AccountGroup";
    public const string Ledger = "Ledger";
    public const string Customer = "Customer";
    public const string Supplier = "Supplier";
    public const string Employee = "Employee";
    public const string Product = "Product";
    public const string Masters = "Masters";
    public const string Loyalty = "Loyalty";
    public const string Offer = "Offer";
    public const string PriceList = "PriceList";
    public const string Bom = "Bom";
    public const string Sales = "Sales";
    public const string Pos = "POS";
    public const string SalesReturn = "SalesReturn";
    public const string Estimate = "Estimate";
    public const string SalesOrder = "SalesOrder";
    public const string DeliveryNote = "DeliveryNote";
    public const string Purchase = "Purchase";
    public const string PurchaseReturn = "PurchaseReturn";
    public const string PurchaseOrder = "PurchaseOrder";
    public const string ReceiptNote = "ReceiptNote";
    public const string OpeningStock = "OpeningStock";
    public const string StockAdjustment = "StockAdjustment";
    public const string Production = "Production";
    public const string Voucher = "Voucher";
    public const string OpeningBalance = "OpeningBalance";
    public const string SalesReports = "SalesReports";
    public const string PurchaseReports = "PurchaseReports";
    public const string StockReports = "StockReports";
    public const string AccountReports = "AccountReports";
    public const string TaxReports = "TaxReports";
    public const string YearEnd = "YearEnd";
    public const string Tools = "Tools";
    public const string Messaging = "Messaging";
    public const string Printing = "Printing";
    /// <summary>Managing the FAQ chatbot's own questions and the unanswered list (using the chatbot needs no right).</summary>
    public const string Faq = "Faq";

    // ---- temple ----
    /// <summary>Vazhipadu counter and bookings.</summary>
    public const string Vazhipadu = "Vazhipadu";
    /// <summary>Daily list of offerings to perform; marking them done.</summary>
    public const string PoojaList = "PoojaList";
    public const string Devotee = "Devotee";
    public const string Donation = "Donation";
    /// <summary>Malayalam calendar corrections and festivals.</summary>
    public const string TempleCalendar = "TempleCalendar";
    /// <summary>Deities and pooja times.</summary>
    public const string TempleMasters = "TempleMasters";

    public static readonly string[] All =
    [
        Dashboard, Company, Settings, Users, Roles, UserLog, AccountGroup, Ledger, Customer, Supplier, Employee,
        Product, Masters, Loyalty, Offer, PriceList, Bom, Sales, Pos, SalesReturn, Estimate, SalesOrder, DeliveryNote,
        Purchase, PurchaseReturn, PurchaseOrder, ReceiptNote, OpeningStock, StockAdjustment, Production, Voucher,
        OpeningBalance, SalesReports, PurchaseReports, StockReports, AccountReports, TaxReports, YearEnd, Tools,
        Messaging, Printing, Faq, Vazhipadu, PoojaList, Devotee, Donation, TempleCalendar, TempleMasters,
    ];

    public static string ForVoucher(VoucherType t) => t switch
    {
        VoucherType.Sales or VoucherType.LocalSales => Sales,
        VoucherType.POSSales => Pos,
        VoucherType.SalesReturn => SalesReturn,
        VoucherType.Estimate => Estimate,
        VoucherType.SalesOrder => SalesOrder,
        VoucherType.DeliveryNote => DeliveryNote,
        VoucherType.Purchase or VoucherType.LocalPurchase => Purchase,
        VoucherType.PurchaseReturn => PurchaseReturn,
        VoucherType.PurchaseOrder => PurchaseOrder,
        VoucherType.ReceiptNote => ReceiptNote,
        VoucherType.OpeningStock => OpeningStock,
        VoucherType.StockAdjustment => StockAdjustment,
        VoucherType.Production => Production,
        VoucherType.Opening => OpeningBalance,
        _ => Voucher,
    };
}
