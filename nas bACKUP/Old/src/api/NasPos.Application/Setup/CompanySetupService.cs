using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Settings;
using NasPos.Domain;
using NasPos.Domain.Entities;

namespace NasPos.Application.Setup;

/// <summary>Creates a company with its chart of accounts, system ledgers, settings and first financial year.</summary>
public class CompanySetupService(IAppDbContext db)
{
    private static readonly (SystemGroup Code, string Name, AccountNature Nature, bool Direct, SystemGroup? Parent)[] Groups =
    [
        (SystemGroup.CurrentAssets, "Current Assets", AccountNature.Asset, false, null),
        (SystemGroup.FixedAssets, "Fixed Assets", AccountNature.Asset, false, null),
        (SystemGroup.SundryDebtors, "Sundry Debtors", AccountNature.Asset, false, SystemGroup.CurrentAssets),
        (SystemGroup.BankAccounts, "Bank Accounts", AccountNature.Asset, false, SystemGroup.CurrentAssets),
        (SystemGroup.CashInHand, "Cash-in-Hand", AccountNature.Asset, false, SystemGroup.CurrentAssets),
        (SystemGroup.CurrentLiabilities, "Current Liabilities", AccountNature.Liability, false, null),
        (SystemGroup.SundryCreditors, "Sundry Creditors", AccountNature.Liability, false, SystemGroup.CurrentLiabilities),
        (SystemGroup.DutiesAndTaxes, "Duties & Taxes", AccountNature.Liability, false, SystemGroup.CurrentLiabilities),
        (SystemGroup.LongTermLiabilities, "Long-term Liabilities", AccountNature.Liability, false, null),
        (SystemGroup.Loans, "Loans", AccountNature.Liability, false, SystemGroup.LongTermLiabilities),
        (SystemGroup.Capital, "Capital Account", AccountNature.Liability, false, null),
        (SystemGroup.DirectIncome, "Direct Income", AccountNature.Income, true, null),
        (SystemGroup.SalesAccounts, "Sales Accounts", AccountNature.Income, true, SystemGroup.DirectIncome),
        (SystemGroup.IndirectIncome, "Indirect Income", AccountNature.Income, false, null),
        (SystemGroup.DirectExpense, "Direct Expense", AccountNature.Expense, true, null),
        (SystemGroup.PurchaseAccounts, "Purchase Accounts", AccountNature.Expense, true, SystemGroup.DirectExpense),
        (SystemGroup.IndirectExpense, "Indirect Expense", AccountNature.Expense, false, null),
        (SystemGroup.Employees, "Employees", AccountNature.Expense, false, SystemGroup.IndirectExpense),
    ];

    private static readonly (SystemLedger Code, string Key, string Name, SystemGroup Group)[] Ledgers =
    [
        (SystemLedger.OpeningBalance, "OPBAL", "Opening Balance Difference", SystemGroup.Capital),
        (SystemLedger.ProfitLoss, "PNL", "Profit & Loss A/c", SystemGroup.Capital),
        (SystemLedger.Sales, "SALES", "Sales", SystemGroup.SalesAccounts),
        (SystemLedger.SalesReturn, "SALESRET", "Sales Return", SystemGroup.SalesAccounts),
        (SystemLedger.Purchase, "PURCHASE", "Purchase", SystemGroup.PurchaseAccounts),
        (SystemLedger.PurchaseReturn, "PURRET", "Purchase Return", SystemGroup.PurchaseAccounts),
        (SystemLedger.Cash, "CASH", "Cash", SystemGroup.CashInHand),
        (SystemLedger.Bank, "BANK", "Bank", SystemGroup.BankAccounts),
        (SystemLedger.DiscountReceived, "DISCRCVD", "Discount Received", SystemGroup.IndirectIncome),
        (SystemLedger.DiscountAllowed, "DISCALWD", "Discount Allowed", SystemGroup.IndirectExpense),
        (SystemLedger.OtherIncome, "OTHINC", "Other Income", SystemGroup.IndirectIncome),
        (SystemLedger.OtherExpense, "OTHEXP", "Other Expense", SystemGroup.IndirectExpense),
        (SystemLedger.RoundOff, "ROUNDOFF", "Round Off", SystemGroup.IndirectExpense),
        (SystemLedger.CashDiscount, "CASHDISC", "Cash Discount", SystemGroup.IndirectExpense),
        (SystemLedger.FreightCoolie, "FREIGHT", "Freight & Coolie", SystemGroup.DirectExpense),
        (SystemLedger.CGSTOutput, "CGSTOUT", "CGST Output", SystemGroup.DutiesAndTaxes),
        (SystemLedger.CGSTInput, "CGSTIN", "CGST Input", SystemGroup.DutiesAndTaxes),
        (SystemLedger.SGSTOutput, "SGSTOUT", "SGST Output", SystemGroup.DutiesAndTaxes),
        (SystemLedger.SGSTInput, "SGSTIN", "SGST Input", SystemGroup.DutiesAndTaxes),
        (SystemLedger.IGSTOutput, "IGSTOUT", "IGST Output", SystemGroup.DutiesAndTaxes),
        (SystemLedger.IGSTInput, "IGSTIN", "IGST Input", SystemGroup.DutiesAndTaxes),
        (SystemLedger.CessOutput, "CESSOUT", "Cess Output", SystemGroup.DutiesAndTaxes),
        (SystemLedger.CessInput, "CESSIN", "Cess Input", SystemGroup.DutiesAndTaxes),
        (SystemLedger.KFCessOutput, "KFCOUT", "KF Cess Output", SystemGroup.DutiesAndTaxes),
        (SystemLedger.KFCessInput, "KFCIN", "KF Cess Input", SystemGroup.DutiesAndTaxes),
        (SystemLedger.ServiceTax, "SRVTAX", "Service Tax", SystemGroup.DutiesAndTaxes),
        (SystemLedger.ServiceCess, "SRVCESS", "Service Cess", SystemGroup.DutiesAndTaxes),
        (SystemLedger.WalkInCustomer, "WALKIN", "Cash Customer", SystemGroup.SundryDebtors),
    ];

    public static (DateOnly Start, DateOnly End, string Name) FinancialYearFor(DateOnly date)
    {
        int startYear = date.Month >= 4 ? date.Year : date.Year - 1;
        return (new DateOnly(startYear, 4, 1), new DateOnly(startYear + 1, 3, 31), $"{startYear}-{(startYear + 1) % 100:00}");
    }

    /// <summary>Seeds everything for a newly inserted company. Caller saves and owns the transaction.</summary>
    public async Task SeedAsync(Company company, DateOnly fyStart, CancellationToken ct = default)
    {
        using var _ = db.IgnoreTenant();
        long cid = company.Id;

        var groups = new Dictionary<SystemGroup, AccountGroup>();
        foreach (var g in Groups)
        {
            var e = new AccountGroup
            {
                CompanyId = cid, Name = g.Name, Nature = g.Nature, IsDirect = g.Direct, SystemCode = g.Code,
                IsSystem = true, Parent = g.Parent is null ? null : groups[g.Parent.Value],
            };
            groups[g.Code] = e;
            db.AccountGroups.Add(e);
        }

        foreach (var l in Ledgers)
            db.Ledgers.Add(new Ledger
            {
                CompanyId = cid, Code = l.Key, Name = l.Name, PrintName = l.Name, AccountGroup = groups[l.Group],
                SystemCode = l.Code, IsSystem = true,
                IsCardLedger = l.Code == SystemLedger.Bank,
                PartyType = l.Code == SystemLedger.WalkInCustomer ? PartyType.Customer : PartyType.None,
                StateCode = company.StateCode,
            });

        foreach (var d in CompanySettings.Definitions)
            db.CompanySettings.Add(new CompanySetting { CompanyId = cid, Key = d.Key, Value = d.Default });

        var fy = new FinancialYear
        {
            CompanyId = cid, StartDate = fyStart, EndDate = fyStart.AddYears(1).AddDays(-1),
            Name = $"{fyStart.Year}-{(fyStart.Year + 1) % 100:00}",
        };
        db.FinancialYears.Add(fy);
        await db.SaveChangesAsync(ct);

        foreach (var (type, prefix) in NumberingService.DefaultPrefixes)
            db.DocumentSequences.Add(new DocumentSequence { CompanyId = cid, FinancialYearId = fy.Id, VoucherType = type, Prefix = prefix });

        foreach (var type in Enum.GetValues<VoucherType>().Where(t => t.IsTradeDocument() || t == VoucherType.Receipt || t == VoucherType.Payment))
        {
            db.InvoiceSettings.Add(new InvoiceSetting { CompanyId = cid, VoucherType = type, OptionsJson = JsonSerializer.Serialize(InvoiceOptions.Default(type)) });
            db.PrintSchemes.Add(new PrintScheme
            {
                CompanyId = cid, VoucherType = type, IsDefault = true,
                Name = type == VoucherType.POSSales ? "Thermal 80mm" : "A4 Standard",
                Format = type == VoucherType.POSSales ? PrintFormat.Thermal80 : PrintFormat.A4,
                TemplateJson = JsonSerializer.Serialize(PrintTemplate.Default(type)),
            });
            if (type == VoucherType.POSSales || type.IsSalesFamily())
                db.PrintSchemes.Add(new PrintScheme
                {
                    CompanyId = cid, VoucherType = type, Name = type == VoucherType.POSSales ? "A4 Standard" : "Thermal 80mm",
                    Format = type == VoucherType.POSSales ? PrintFormat.A4 : PrintFormat.Thermal80,
                    TemplateJson = JsonSerializer.Serialize(PrintTemplate.Default(type)),
                });
        }

        db.LabelTemplates.Add(new LabelTemplate { CompanyId = cid, Name = "50 x 25 mm (2 across)", IsDefault = true, FieldsJson = JsonSerializer.Serialize(new LabelFields()) });
        foreach (var ev in new[] { "Invoice", "Receipt", "Birthday", "Payment Reminder", "Offer" })
            db.SmsEvents.Add(new SmsEvent { CompanyId = cid, Name = ev });
        await db.SaveChangesAsync(ct);

        var invoiceEvent = await db.SmsEvents.FirstAsync(e => e.CompanyId == cid && e.Name == "Invoice", ct);
        db.SmsTemplates.Add(new SmsTemplate
        {
            CompanyId = cid, EventId = invoiceEvent.Id, Name = "Invoice SMS",
            Body = "Dear {CustomerName}, thank you for shopping at {CompanyName}. Bill {BillNo} dated {BillDate} for Rs.{BillAmount}.",
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ensures a company created before a new system ledger was introduced gets it.</summary>
    public async Task EnsureSystemLedgersAsync(long companyId, CancellationToken ct = default)
    {
        using var _ = db.IgnoreTenant();
        var have = await db.Ledgers.Where(l => l.CompanyId == companyId && l.SystemCode != null).Select(l => l.SystemCode!.Value).ToListAsync(ct);
        var groups = await db.AccountGroups.Where(g => g.CompanyId == companyId && g.SystemCode != null).ToDictionaryAsync(g => g.SystemCode!.Value, ct);
        foreach (var l in Ledgers.Where(l => !have.Contains(l.Code)))
            db.Ledgers.Add(new Ledger { CompanyId = companyId, Code = l.Key, Name = l.Name, PrintName = l.Name, AccountGroupId = groups[l.Group].Id, SystemCode = l.Code, IsSystem = true });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Per-voucher-type screen options.</summary>
public sealed class InvoiceOptions
{
    public bool PrintOnSave { get; set; }
    public bool ConfirmPrint { get; set; } = true;
    public bool ShowPreview { get; set; }
    public bool InclusiveUnitPrice { get; set; }
    public bool FreeQty { get; set; } = true;
    public bool ExtraColumn { get; set; }
    public string ExtraColumnName { get; set; } = "Remarks";
    public bool ProductRemarks { get; set; }
    public bool DisableRateEdit { get; set; }
    public bool ScannerMode { get; set; }
    public bool CashDeskOnSave { get; set; }
    public long? PrintSchemeId { get; set; }
    public bool FocusNextRow { get; set; } = true;
    public bool CustomerAutoSave { get; set; } = true;
    public bool DeliveryDate { get; set; }
    public int AmountDecimals { get; set; } = 2;
    public int QtyDecimals { get; set; } = 2;
    public bool SendSms { get; set; }
    public string? SmsTemplate { get; set; }
    public string[] HiddenColumns { get; set; } = [];

    public static InvoiceOptions Default(VoucherType t) => new()
    {
        PrintOnSave = t == VoucherType.POSSales,
        ScannerMode = t == VoucherType.POSSales,
        InclusiveUnitPrice = t.IsSalesFamily(),
        CashDeskOnSave = t == VoucherType.POSSales,
        DeliveryDate = t is VoucherType.SalesOrder or VoucherType.PurchaseOrder,
    };
}

public sealed class PrintTemplate
{
    public string Title { get; set; } = "TAX INVOICE";
    public bool ShowLogo { get; set; } = true;
    public bool ShowCompanyAddress { get; set; } = true;
    public bool ShowGstin { get; set; } = true;
    public bool ShowParty { get; set; } = true;
    public bool ShowTaxSummary { get; set; } = true;
    public bool ShowAmountInWords { get; set; } = true;
    public bool ShowYouSaved { get; set; } = true;
    public bool ShowRegionalName { get; set; }
    public bool ShowSignature { get; set; } = true;
    public string[] Columns { get; set; } = ["SlNo", "Item", "Hsn", "Qty", "Rate", "Disc", "Taxable", "Gst", "Total"];
    public string[] HeaderLines { get; set; } = [];
    public string[] FooterLines { get; set; } = ["Thank you. Visit again!"];

    public static PrintTemplate Default(VoucherType t) => new()
    {
        Title = t switch
        {
            VoucherType.Estimate => "ESTIMATE",
            VoucherType.SalesOrder => "SALES ORDER",
            VoucherType.PurchaseOrder => "PURCHASE ORDER",
            VoucherType.SalesReturn => "CREDIT NOTE (SALES RETURN)",
            VoucherType.PurchaseReturn => "DEBIT NOTE (PURCHASE RETURN)",
            VoucherType.DeliveryNote => "DELIVERY NOTE",
            VoucherType.ReceiptNote => "GOODS RECEIPT NOTE",
            VoucherType.Purchase or VoucherType.LocalPurchase => "PURCHASE",
            VoucherType.Receipt => "RECEIPT",
            VoucherType.Payment => "PAYMENT VOUCHER",
            VoucherType.OpeningStock => "OPENING STOCK",
            VoucherType.StockAdjustment => "STOCK ADJUSTMENT",
            VoucherType.Production => "PRODUCTION",
            _ => "TAX INVOICE",
        },
        Columns = t == VoucherType.POSSales ? ["Item", "Qty", "Rate", "Total"] : ["SlNo", "Item", "Hsn", "Qty", "Rate", "Disc", "Taxable", "Gst", "Total"],
    };
}

public sealed class LabelFields
{
    public bool ShowCompany { get; set; } = true;
    public bool ShowName { get; set; } = true;
    public bool ShowMrp { get; set; } = true;
    public bool ShowSaleRate { get; set; } = true;
    public bool ShowBarcode { get; set; } = true;
    public bool ShowBarcodeText { get; set; } = true;
    public bool ShowExpiry { get; set; }
    public bool ShowPackedDate { get; set; }
    public decimal FontSize { get; set; } = 7;
}
