using System.Globalization;
using NasPos.Domain;
using NasPos.Domain.Validation;

namespace NasPos.Application.Settings;

public enum SettingType { Bool, Int, Decimal, Text, Choice, Date }

public sealed record SettingDefinition(string Key, string Label, string Group, SettingType Type, string Default,
    string[]? Options = null, string? Help = null);

/// <summary>Typed view over the company's key/value feature switches.</summary>
public sealed class CompanySettings(IReadOnlyDictionary<string, string?> values)
{
    public static readonly SettingDefinition[] Definitions =
    [
        new("EnableBatch", "Batch / barcode tracking", "Inventory", SettingType.Bool, "true"),
        new("EnableUnitConversion", "Alternate units (unit conversion)", "Inventory", SettingType.Bool, "false"),
        new("EnableMultiMrp", "Multiple MRP per batch", "Inventory", SettingType.Bool, "false"),
        new("EnableExpiry", "Expiry dates", "Inventory", SettingType.Bool, "false"),
        new("EnableProduction", "Production / BOM", "Inventory", SettingType.Bool, "false"),
        new("NegativeStockMode", "Negative stock", "Inventory", SettingType.Choice, "Warn", ["Allow", "Warn", "Block"]),
        new("AutoBatchPrefix", "Auto batch/barcode prefix", "Inventory", SettingType.Text, "B"),
        new("BarcodePrefix", "Generated item barcode prefix (EAN-13, 2–3 digits from 200–299)", "Inventory", SettingType.Text, "200"),
        new("UpdateRatesOnPurchase", "Update product rates on purchase", "Purchase", SettingType.Bool, "true"),
        new("UpdateDefaultBarcode", "Update default barcode on purchase", "Purchase", SettingType.Bool, "false"),
        new("UpdateRatesOnProduction", "Update product rates on production", "Purchase", SettingType.Bool, "false"),
        new("InputTaxClaimable", "Input tax is claimable (registered dealer)", "Purchase", SettingType.Bool, "true"),
        new("DefaultCostFactor", "Default cost factor", "Purchase", SettingType.Decimal, "1"),
        new("ShowSupplierRates", "Show supplier past rates", "Purchase", SettingType.Bool, "true"),
        new("EnablePriceList", "Price lists", "Sales", SettingType.Bool, "false"),
        new("EnableItemOffers", "Item offers", "Sales", SettingType.Bool, "false"),
        new("EnableLoyalty", "Loyalty points", "Sales", SettingType.Bool, "false"),
        new("CreditLimitMode", "Credit limit", "Sales", SettingType.Choice, "Warn", ["Ignore", "Warn", "Block"]),
        new("RequireSalesperson", "Salesperson required on sales", "Sales", SettingType.Bool, "true"),
        new("ShowCustomerRates", "Show customer past rates", "Sales", SettingType.Bool, "true"),
        new("ShowPosImages", "Show item images in POS", "Sales", SettingType.Bool, "true"),
        new("DefaultRoundOff", "Default round-off", "Sales", SettingType.Choice, "Auto", ["Auto", "Up", "Down", "None"]),
        new("EnableBillByBill", "Bill-by-bill outstanding (ageing)", "Accounts", SettingType.Bool, "true"),
        new("EnableCess", "GST cess", "Tax", SettingType.Bool, "false"),
        new("EnableAddCess", "Additional cess per unit", "Tax", SettingType.Bool, "false"),
        new("EnableKfCess", "Kerala flood cess", "Tax", SettingType.Bool, "false"),
        new("KfCessFrom", "KF cess from", "Tax", SettingType.Date, "2019-08-01"),
        new("KfCessTo", "KF cess to", "Tax", SettingType.Date, "2021-07-31"),
        new("ServiceTaxRate", "Service tax %", "Tax", SettingType.Decimal, "0"),
        new("ServiceCessRate", "Service cess %", "Tax", SettingType.Decimal, "0"),
        new("MultiGstDisplay", "Show tax split on bill", "Tax", SettingType.Bool, "true"),
        new("EnableWeighingScale", "Weighing-scale barcodes", "Devices", SettingType.Bool, "false"),
        new("WeighingScaleFormat", "Scale format (prefix|code len|value len|divisor)", "Devices", SettingType.Text, "21|5|5|1000"),
        new("EnablePoleDisplay", "Pole (customer) display", "Devices", SettingType.Bool, "false"),
        new("PoleDisplayBaud", "Pole display baud rate", "Devices", SettingType.Int, "9600"),
        new("PoleDisplayWelcome", "Pole display welcome text", "Devices", SettingType.Text, "WELCOME"),
        new("EnableSms", "SMS", "Messaging", SettingType.Bool, "false"),
        new("EnableCustomerSms", "Send bill SMS to customers", "Messaging", SettingType.Bool, "false"),
        new("EnableEmail", "E-mail invoices", "Messaging", SettingType.Bool, "false"),
        new("QtyDecimals", "Quantity decimals", "Display", SettingType.Int, "2"),
        new("AmountDecimals", "Amount decimals", "Display", SettingType.Int, "2"),
        new("SupportEmail", "Support e-mail (FAQ › Email us sends here)", "Help & support", SettingType.Text, ""),
        new("SupportPhone", "Support phone (shown on FAQ › Email us)", "Help & support", SettingType.Text, ""),
    ];

    private string Get(string key) =>
        values.TryGetValue(key, out var v) && v is not null ? v : Definitions.FirstOrDefault(d => d.Key == key)?.Default ?? "";

    private bool B(string key) => bool.TryParse(Get(key), out var b) && b;
    private int I(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
    private decimal D(string key) => decimal.TryParse(Get(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private DateOnly? Dt(string key) => DateOnly.TryParse(Get(key), CultureInfo.InvariantCulture, out var d) ? d : null;
    private T E<T>(string key) where T : struct, Enum => Enum.TryParse<T>(Get(key), true, out var e) ? e : default;

    public bool EnableBatch => B("EnableBatch");
    public bool EnableUnitConversion => B("EnableUnitConversion");
    public bool EnableMultiMrp => B("EnableMultiMrp");
    public bool EnableExpiry => B("EnableExpiry");
    public bool EnableProduction => B("EnableProduction");
    public NegativeStockMode NegativeStockMode => E<NegativeStockMode>("NegativeStockMode");
    public string AutoBatchPrefix => Get("AutoBatchPrefix");
    public string BarcodePrefix => Get("BarcodePrefix");
    public bool UpdateRatesOnPurchase => B("UpdateRatesOnPurchase");
    public bool UpdateDefaultBarcode => B("UpdateDefaultBarcode");
    public bool UpdateRatesOnProduction => B("UpdateRatesOnProduction");
    public bool InputTaxClaimable => B("InputTaxClaimable");
    public decimal DefaultCostFactor => D("DefaultCostFactor");
    public bool EnablePriceList => B("EnablePriceList");
    public bool EnableItemOffers => B("EnableItemOffers");
    public bool EnableLoyalty => B("EnableLoyalty");
    public CreditLimitMode CreditLimitMode => E<CreditLimitMode>("CreditLimitMode");
    public bool RequireSalesperson => B("RequireSalesperson");
    public RoundOffMode DefaultRoundOff => E<RoundOffMode>("DefaultRoundOff");
    public bool EnableBillByBill => B("EnableBillByBill");
    public bool EnableCess => B("EnableCess");
    public bool EnableAddCess => B("EnableAddCess");
    public bool EnableKfCess => B("EnableKfCess");
    public DateOnly? KfCessFrom => Dt("KfCessFrom");
    public DateOnly? KfCessTo => Dt("KfCessTo");
    public decimal ServiceTaxRate => D("ServiceTaxRate");
    public decimal ServiceCessRate => D("ServiceCessRate");
    public bool EnableWeighingScale => B("EnableWeighingScale");
    public ScaleBarcodeFormat WeighingScaleFormat => ScaleBarcodeFormat.Parse(Get("WeighingScaleFormat"));
    public bool EnableSms => B("EnableSms");
    public bool EnableCustomerSms => B("EnableCustomerSms");
    public bool EnableEmail => B("EnableEmail");
    public int QtyDecimals => I("QtyDecimals");
    public int AmountDecimals => I("AmountDecimals");
    public string SupportEmail => Get("SupportEmail").Trim();
    public string SupportPhone => Get("SupportPhone").Trim();

    public bool KfCessAppliesOn(DateOnly date) =>
        EnableKfCess && (KfCessFrom is null || date >= KfCessFrom) && (KfCessTo is null || date <= KfCessTo);

    /// <summary>All values (defaults filled in) for the client.</summary>
    public Dictionary<string, string> ToDictionary() => Definitions.ToDictionary(d => d.Key, d => Get(d.Key));
}
