using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Kshethra.Application.Common;
using Kshethra.Application.Documents;
using Kshethra.Domain;
using Kshethra.Domain.Entities;

namespace Kshethra.Application.Tools;

/// <summary>Runs work inside a DI scope whose current user is switched to another company (for cross-company copies).</summary>
public interface ITenantScopeFactory
{
    Task<T> RunAsAsync<T>(long companyId, Func<IServiceProvider, Task<T>> work);
}

public sealed record TransferRequest(long TargetCompanyId, long[] ProductIds, long[] LedgerIds, long[] DocumentIds, bool AllProducts, bool AllLedgers);
public sealed record TransferResult(int Products, int Ledgers, int Documents);

public class TransferService(IAppDbContext db, ICurrentUser user, ITenantScopeFactory tenants, AuditService audit)
{
    private sealed record ProductData(Product P, string? Category, string? Manufacturer);
    private sealed record LedgerData(Ledger L, SystemGroup? Group, string GroupName);

    /// <summary>Copies selected items, ledgers and documents into another company in one transaction there.</summary>
    public async Task<TransferResult> TransferAsync(TransferRequest r, CancellationToken ct)
    {
        if (r.TargetCompanyId == user.CompanyId) throw new DomainException("Choose a different temple.");
        using (db.IgnoreTenant())
        {
            if (!await db.UserCompanies.AnyAsync(m => m.UserId == user.UserId && m.CompanyId == r.TargetCompanyId && m.Role!.IsActive, ct))
                throw new ForbiddenException("You are not a member of the target temple.");
        }

        var docs = await db.TradeDocuments.AsNoTracking().Include(d => d.Lines).Where(d => r.DocumentIds.Contains(d.Id) && !d.Cancelled).ToListAsync(ct);
        var productIds = (r.AllProducts ? await db.Products.Select(p => p.Id).ToListAsync(ct) : r.ProductIds.ToList())
            .Union(docs.SelectMany(d => d.Lines.Select(l => l.ProductId))).ToList();
        var ledgerIds = (r.AllLedgers ? await db.Ledgers.Where(l => !l.IsSystem).Select(l => l.Id).ToListAsync(ct) : r.LedgerIds.ToList())
            .Union(docs.Where(d => d.PartyLedgerId != null).Select(d => d.PartyLedgerId!.Value)).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new ProductData(p, p.Category != null ? p.Category.Name : null, p.Manufacturer != null ? p.Manufacturer.Name : null)).ToListAsync(ct);
        var ledgers = await db.Ledgers.AsNoTracking().Where(l => ledgerIds.Contains(l.Id))
            .Select(l => new LedgerData(l, l.AccountGroup!.SystemCode, l.AccountGroup.Name)).ToListAsync(ct);
        var sourceCodes = products.ToDictionary(p => p.P.Id, p => p.P.Code);
        var sourceLedgerCodes = ledgers.ToDictionary(l => l.L.Id, l => l.L.Code);
        var sourceSystem = await db.Ledgers.AsNoTracking().Where(l => l.SystemCode != null).ToDictionaryAsync(l => l.Id, l => l.SystemCode!.Value, ct);

        var result = await tenants.RunAsAsync(r.TargetCompanyId, async sp =>
        {
            var tdb = sp.GetRequiredService<IAppDbContext>();
            var tdocs = sp.GetRequiredService<DocumentService>();
            await using var tx = await tdb.BeginAsync(ct);
            var cats = await tdb.Categories.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, ct);
            var mnfs = await tdb.Manufacturers.ToDictionaryAsync(c => c.Name, StringComparer.OrdinalIgnoreCase, ct);
            var tproducts = await tdb.Products.ToDictionaryAsync(p => p.Code, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var src in products)
            {
                if (!tproducts.TryGetValue(src.P.Code, out var p))
                {
                    p = new Product { Code = src.P.Code };
                    tdb.Products.Add(p);
                    tproducts[p.Code] = p;
                }
                CopyProduct(src.P, p);
                if (src.Category is not null)
                {
                    if (!cats.TryGetValue(src.Category, out var c)) { c = new Category { Name = src.Category }; tdb.Categories.Add(c); cats[c.Name] = c; }
                    p.Category = c;
                }
                if (src.Manufacturer is not null)
                {
                    if (!mnfs.TryGetValue(src.Manufacturer, out var m)) { m = new Manufacturer { Name = src.Manufacturer }; tdb.Manufacturers.Add(m); mnfs[m.Name] = m; }
                    p.Manufacturer = m;
                }
            }
            var groups = await tdb.AccountGroups.ToListAsync(ct);
            var tledgers = await tdb.Ledgers.ToDictionaryAsync(l => l.Code, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var src in ledgers.Where(l => !l.L.IsSystem))
            {
                var group = groups.FirstOrDefault(g => src.Group != null && g.SystemCode == src.Group) ?? groups.FirstOrDefault(g => g.Name == src.GroupName)
                            ?? groups.First(g => g.SystemCode == (src.L.PartyType == PartyType.Supplier ? SystemGroup.SundryCreditors : SystemGroup.SundryDebtors));
                if (!tledgers.TryGetValue(src.L.Code, out var l))
                {
                    if (!src.L.IsCorporate && await tdb.Ledgers.AnyAsync(x => x.Name == src.L.Name && !x.IsCorporate, ct)) continue;
                    l = new Ledger { Code = src.L.Code };
                    tdb.Ledgers.Add(l);
                    tledgers[l.Code] = l;
                }
                l.Name = src.L.Name; l.PrintName = src.L.PrintName; l.AccountGroupId = group.Id; l.PartyType = src.L.PartyType; l.Address = src.L.Address;
                l.City = src.L.City; l.StateCode = src.L.StateCode; l.Phone = src.L.Phone; l.Mobile = src.L.Mobile; l.Email = src.L.Email; l.Gstin = src.L.Gstin;
                l.CreditLimit = src.L.CreditLimit; l.CreditDays = src.L.CreditDays; l.DiscountPercent = src.L.DiscountPercent; l.IsActive = src.L.IsActive;
                l.IsCorporate = src.L.IsCorporate;
            }
            await tdb.SaveChangesAsync(ct);

            var tsystem = await tdb.Ledgers.Where(l => l.SystemCode != null).ToDictionaryAsync(l => l.SystemCode!.Value, l => l.Id, ct);
            int copied = 0;
            foreach (var d in docs.OrderBy(d => d.Date))
            {
                long? party = d.PartyLedgerId is null ? null
                    : sourceSystem.TryGetValue(d.PartyLedgerId.Value, out var sys) ? tsystem.GetValueOrDefault(sys)
                    : sourceLedgerCodes.TryGetValue(d.PartyLedgerId.Value, out var lc) && tledgers.TryGetValue(lc, out var tl) ? tl.Id : null;
                var dto = new TradeDocumentDto
                {
                    VoucherType = d.VoucherType, Date = d.Date, PartyLedgerId = party, PartyName = d.PartyName, PartyAddress = d.PartyAddress,
                    PartyPhone = d.PartyPhone, PartyGstin = d.PartyGstin, PartyStateCode = d.PartyStateCode,
                    PaymentMode = party is null && d.PaymentMode != PaymentMode.Cash ? PaymentMode.Cash : d.PaymentMode,
                    TaxType = d.TaxType, BillType = d.BillType, Remarks = $"Copied from {d.DocumentNo}. {d.Remarks}".Trim(), ReferenceNo = d.ReferenceNo,
                    BillDiscount = d.BillDiscount, BillDiscountPercent = d.BillDiscountPercent, OtherExpense = d.OtherExpense, OtherExpenseTaxRate = d.OtherExpenseTaxRate,
                    Freight = d.Freight, FreightTaxRate = d.FreightTaxRate, RoundOffMode = d.RoundOffMode, CashDiscount = d.CashDiscount,
                    CashDiscountPercent = d.CashDiscountPercent, CostFactor = d.CostFactor, CashAmount = d.CashAmount, CardAmount = d.CardAmount,
                    CreditAmount = d.CreditAmount, AcknowledgeWarnings = true,
                    Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new TradeLineDto
                    {
                        ProductId = tproducts[sourceCodes[l.ProductId]].Id, BatchCode = l.BatchCode, Direction = l.Direction, IsAltUnit = l.IsAltUnit,
                        Qty = l.Qty, FreeQty = l.FreeQty, Mrp = l.Mrp, Rate = l.Rate, TaxInclusive = l.TaxInclusive, DiscountPercent = l.DiscountPercent,
                        DiscountAmount = l.DiscountAmount, SaleRate = l.SaleRate, ExpiryDate = l.ExpiryDate,
                    }).ToList(),
                };
                if (d.VoucherType.IsSalesFamily()) dto.SalespersonId = await tdb.Employees.Where(e => e.IsActive).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);
                await tdocs.SaveAsync(dto, null, ct);
                copied++;
            }
            await tx.CommitAsync(ct);
            return new TransferResult(products.Count, ledgers.Count(l => !l.L.IsSystem), copied);
        });
        audit.Log(Screens.Tools, "Export", $"Copied {result.Products} items, {result.Ledgers} ledgers, {result.Documents} documents to company #{r.TargetCompanyId}");
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static void CopyProduct(Product s, Product t)
    {
        t.Name = s.Name; t.RegionalName = s.RegionalName; t.HsnCode = s.HsnCode; t.ItemClass = s.ItemClass; t.CgstRate = s.CgstRate; t.SgstRate = s.SgstRate;
        t.IgstRate = s.IgstRate; t.HasSecondaryTax = s.HasSecondaryTax; t.SecondaryTaxThreshold = s.SecondaryTaxThreshold; t.Cgst2Rate = s.Cgst2Rate;
        t.Sgst2Rate = s.Sgst2Rate; t.Igst2Rate = s.Igst2Rate; t.CessRate = s.CessRate; t.AddCessPerUnit = s.AddCessPerUnit; t.KfCessRate = s.KfCessRate;
        t.SaleTaxInclusive = s.SaleTaxInclusive; t.PurchaseTaxInclusive = s.PurchaseTaxInclusive; t.PurchaseRate = s.PurchaseRate; t.SaleRate = s.SaleRate;
        t.Mrp = s.Mrp; t.SaleMarginPercent = s.SaleMarginPercent; t.MrpMarginPercent = s.MrpMarginPercent; t.DiscountPercent = s.DiscountPercent;
        t.MainUnit = s.MainUnit; t.AltUnit = s.AltUnit; t.ConversionFactor = s.ConversionFactor; t.DefaultBarcode = s.DefaultBarcode; t.ReorderLevel = s.ReorderLevel;
        t.Rack = s.Rack; t.TrackExpiry = s.TrackExpiry; t.IsScaleItem = s.IsScaleItem; t.PluCode = s.PluCode; t.PiecesPerBox = s.PiecesPerBox;
        t.SqftPerBox = s.SqftPerBox; t.Remarks = s.Remarks; t.IsActive = s.IsActive;
    }
}

public sealed record ResetChallenge(string Code, DateTimeOffset ExpiresAt, string CompanyName);
public sealed record ResetConfirm(string Code, string CompanyName);

/// <summary>
/// Guarded "reset a company's transactions": step 1 issues a one-time code; step 2 needs the code, the company name typed
/// exactly, and a step-up re-authentication (enforced by the API). Masters are kept. Fully audited; never arbitrary SQL.
/// </summary>
public class ResetService(IAppDbContext db, ICurrentUser user, IMemoryCache cache, AuditService audit, IClock clock)
{
    private string Key => $"reset:{user.CompanyId}:{user.UserId}";

    public async Task<ResetChallenge> RequestAsync(CancellationToken ct)
    {
        if (!user.IsAdmin) throw new ForbiddenException("Only an administrator can reset temple data.");
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        var expires = clock.Now.AddMinutes(10);
        cache.Set(Key, code, TimeSpan.FromMinutes(10));
        audit.Log(Screens.Tools, "ResetRequested", company.Name);
        await db.SaveChangesAsync(ct);
        return new ResetChallenge(code, expires, company.Name);
    }

    public async Task ConfirmAsync(ResetConfirm r, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new ForbiddenException("Only an administrator can reset temple data.");
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        if (!cache.TryGetValue<string>(Key, out var expected) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected ?? ""), System.Text.Encoding.UTF8.GetBytes((r.Code ?? "").Trim().ToUpperInvariant())))
            throw new DomainException("The confirmation code is wrong or has expired. Start again.", "reset_code");
        if (!string.Equals(r.CompanyName?.Trim(), company.Name, StringComparison.Ordinal))
            throw new DomainException("Type the temple name exactly as shown.", "reset_name");
        cache.Remove(Key);

        await using var tx = await db.BeginAsync(System.Data.IsolationLevel.Serializable, ct);
        await db.BillSettlements.Where(s => db.BillOutstandings.Any(b => b.Id == s.BillId)).ExecuteDeleteAsync(ct);
        await db.BillOutstandings.ExecuteDeleteAsync(ct);
        await db.AccountEntries.ExecuteDeleteAsync(ct);
        await db.StockMovements.ExecuteDeleteAsync(ct);
        await db.LoyaltyTransactions.ExecuteDeleteAsync(ct);
        await db.VoucherLines.Where(l => db.Vouchers.Any(v => v.Id == l.VoucherId)).ExecuteDeleteAsync(ct);
        await db.Vouchers.ExecuteDeleteAsync(ct);
        await db.TradeDocuments.Where(d => d.SourceOrderId != null).ExecuteUpdateAsync(s => s.SetProperty(d => d.SourceOrderId, (long?)null), ct);
        await db.TradeDocumentLines.Where(l => db.TradeDocuments.Any(d => d.Id == l.DocumentId)).ExecuteDeleteAsync(ct);
        await db.TradeDocuments.ExecuteDeleteAsync(ct);
        await db.VoidItems.ExecuteDeleteAsync(ct);
        await db.DocumentSequences.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextNumber, 1L), ct);
        audit.Log(Screens.Tools, "Reset", $"All transactions of {company.Name} deleted by {user.UserName}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
