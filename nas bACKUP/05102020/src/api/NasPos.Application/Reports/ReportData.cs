using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Domain;

namespace NasPos.Application.Reports;

public sealed record LineRow(
    long DocId, string DocNo, DateTimeOffset Date, VoucherType VoucherType, long? PartyId, string? PartyName, string? PartyGstin,
    string? PartyState, PaymentMode PaymentMode, TaxType TaxType, BillType BillType, long? SalespersonId, string? Counter, long? UserId,
    long ProductId, string ProductCode, string ProductName, string? Hsn, long? CategoryId, string? CategoryName, long? ManufacturerId,
    string? ManufacturerName, ItemClass ItemClass, string? Unit, decimal UnitFactor, decimal Qty, decimal Free, decimal Rate, decimal Mrp,
    decimal Gross, decimal Discount, decimal Taxable, decimal CgstRate, decimal Cgst, decimal SgstRate, decimal Sgst, decimal IgstRate,
    decimal Igst, decimal CessRate, decimal Cess, decimal AddCess, decimal KfCessRate, decimal KfCess, decimal LineTotal, decimal Cost,
    decimal Profit, string? Batch, decimal CommissionPercent, StockDirection Direction)
{
    public decimal GstRate => CgstRate + SgstRate + IgstRate;
    public decimal Tax => Cgst + Sgst + Igst + Cess + AddCess + KfCess;
    public decimal BaseQty => (Qty + Free) * UnitFactor;
    public decimal CostTotal => Cost * BaseQty;
}

/// <summary>Shared, parameterised data loaders for the report providers (all tenant-filtered by EF).</summary>
public class ReportData(IAppDbContext db, ICurrentUser user, IClock clock)
{
    public static readonly VoucherType[] SalesTypes = [VoucherType.Sales, VoucherType.LocalSales, VoucherType.POSSales];
    public static readonly VoucherType[] PurchaseTypes = [VoucherType.Purchase, VoucherType.LocalPurchase];

    public IAppDbContext Db => db;
    public IClock Clock => clock;
    public long FinancialYearId => user.FinancialYearId;
    public long CompanyId => user.CompanyId;

    public async Task<(DateOnly From, DateOnly To)> RangeAsync(ReportFilter f, CancellationToken ct)
    {
        var fy = await db.FinancialYears.AsNoTracking().FirstAsync(x => x.Id == user.FinancialYearId, ct);
        var today = clock.Today();
        var to = f.To ?? (today < fy.EndDate ? (today < fy.StartDate ? fy.StartDate : today) : fy.EndDate);
        var from = f.From ?? fy.StartDate;
        if (from > to) (from, to) = (to, from);
        return (from, to);
    }

    public async Task<string> RangeTextAsync(ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(f, ct);
        return $"{from:dd-MM-yyyy} to {to:dd-MM-yyyy}";
    }

    public async Task<List<LineRow>> LinesAsync(VoucherType[] types, ReportFilter f, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(f, ct);
        var start = clock.DayStart(from);
        var end = clock.DayEnd(to);
        var docs = db.TradeDocuments.AsNoTracking().Where(d => !d.Cancelled && types.Contains(d.VoucherType) && d.Date >= start && d.Date < end);
        if (f.VoucherType is not null && types.Contains(f.VoucherType.Value)) docs = docs.Where(d => d.VoucherType == f.VoucherType);
        if (f.PaymentMode is not null) docs = docs.Where(d => d.PaymentMode == f.PaymentMode);
        if (f.TaxType is not null) docs = docs.Where(d => d.TaxType == f.TaxType);
        if (f.BillType is not null) docs = docs.Where(d => d.BillType == f.BillType);
        if (f.PartyId is not null) docs = docs.Where(d => d.PartyLedgerId == f.PartyId);
        if (f.SalespersonId is not null) docs = docs.Where(d => d.SalespersonId == f.SalespersonId);
        if (!string.IsNullOrWhiteSpace(f.Counter)) docs = docs.Where(d => d.Counter == f.Counter);
        if (f.UserId is not null) docs = docs.Where(d => d.UserId == f.UserId);
        if (f.AreaId is not null) docs = docs.Where(d => d.PartyLedger != null && d.PartyLedger.AreaId == f.AreaId);

        var q = from d in docs
                join l in db.TradeDocumentLines on d.Id equals l.DocumentId
                join p in db.Products on l.ProductId equals p.Id
                select new { d, l, p, Cat = p.Category != null ? p.Category.Name : null, Mnf = p.Manufacturer != null ? p.Manufacturer.Name : null };
        if (f.CategoryId is not null) q = q.Where(x => x.p.CategoryId == f.CategoryId);
        if (f.ProductId is not null) q = q.Where(x => x.p.Id == f.ProductId);
        if (f.ManufacturerId is not null) q = q.Where(x => x.p.ManufacturerId == f.ManufacturerId);
        if (f.ItemClass is not null) q = q.Where(x => x.l.ItemClass == f.ItemClass);

        var rows = await q.OrderBy(x => x.d.Date).ThenBy(x => x.d.Id).ThenBy(x => x.l.LineNo).Select(x => new LineRow(
            x.d.Id, x.d.DocumentNo, x.d.Date, x.d.VoucherType, x.d.PartyLedgerId, x.d.PartyName, x.d.PartyGstin, x.d.PartyStateCode,
            x.d.PaymentMode, x.d.TaxType, x.d.BillType, x.d.SalespersonId, x.d.Counter, x.d.UserId,
            x.p.Id, x.p.Code, x.p.Name, x.l.HsnCode, x.p.CategoryId, x.Cat, x.p.ManufacturerId, x.Mnf, x.l.ItemClass, x.l.Unit, x.l.UnitFactor,
            x.l.Qty, x.l.FreeQty, x.l.Rate, x.l.Mrp, x.l.Gross, x.l.DiscountAmount + x.l.BillDiscountShare, x.l.Taxable,
            x.l.CgstRate, x.l.CgstAmount, x.l.SgstRate, x.l.SgstAmount, x.l.IgstRate, x.l.IgstAmount, x.l.CessRate, x.l.CessAmount,
            x.l.AddCessAmount, x.l.KfCessRate, x.l.KfCessAmount, x.l.LineTotal, x.l.Cost, x.l.Profit, x.l.BatchCode, x.p.CommissionPercent,
            x.l.Direction)).ToListAsync(ct);
        return rows;
    }

    public async Task<List<Domain.Entities.TradeDocument>> DocsAsync(VoucherType[] types, ReportFilter f, bool includeCancelled, CancellationToken ct)
    {
        var (from, to) = await RangeAsync(f, ct);
        var start = clock.DayStart(from);
        var end = clock.DayEnd(to);
        var q = db.TradeDocuments.AsNoTracking().Where(d => types.Contains(d.VoucherType) && d.Date >= start && d.Date < end);
        if (!includeCancelled) q = q.Where(d => !d.Cancelled);
        if (f.VoucherType is not null && types.Contains(f.VoucherType.Value)) q = q.Where(d => d.VoucherType == f.VoucherType);
        if (f.PaymentMode is not null) q = q.Where(d => d.PaymentMode == f.PaymentMode);
        if (f.TaxType is not null) q = q.Where(d => d.TaxType == f.TaxType);
        if (f.BillType is not null) q = q.Where(d => d.BillType == f.BillType);
        if (f.PartyId is not null) q = q.Where(d => d.PartyLedgerId == f.PartyId);
        if (f.SalespersonId is not null) q = q.Where(d => d.SalespersonId == f.SalespersonId);
        if (!string.IsNullOrWhiteSpace(f.Counter)) q = q.Where(d => d.Counter == f.Counter);
        if (f.UserId is not null) q = q.Where(d => d.UserId == f.UserId);
        if (f.AreaId is not null) q = q.Where(d => d.PartyLedger != null && d.PartyLedger.AreaId == f.AreaId);
        return await q.OrderBy(d => d.Date).ThenBy(d => d.Id).ToListAsync(ct);
    }

    public async Task<Dictionary<long, string>> UserNamesAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UserName, ct);

    public async Task<Dictionary<long, string>> EmployeeNamesAsync(CancellationToken ct) =>
        await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, ct);

    public string D(DateTimeOffset t) => clock.BusinessDate(t).ToString("yyyy-MM-dd");
}
