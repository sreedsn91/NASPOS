using System.Data;
using Microsoft.EntityFrameworkCore;
using Kshethra.Application.Common;
using Kshethra.Application.Inventory;
using Kshethra.Application.Licensing;
using Kshethra.Application.Messaging;
using Kshethra.Application.Settings;
using Kshethra.Domain;
using Kshethra.Domain.Calculation;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Posting;

namespace Kshethra.Application.Documents;

/// <summary>
/// Save / edit / cancel of every inventory document. One database transaction covers the header, lines, batches,
/// account legs, stock movements, bill outstanding, loyalty, rate updates and the audit row: all or nothing.
/// </summary>
public class DocumentService(
    IAppDbContext db, ICurrentUser user, IClock clock, SettingsService settingsService, NumberingService numbering,
    FinancialYearService years, SystemLedgerResolver systemLedgers, StockQueries stock, LedgerQueries ledgers,
    AuditService audit, IRealtimeNotifier notifier, DocumentMessaging messaging, LicenseService license)
{
    public async Task<TradeDocumentDto> SaveAsync(TradeDocumentDto dto, long? id, CancellationToken ct)
    {
        if (!dto.VoucherType.IsTradeDocument()) throw new DomainException("Use the voucher endpoint for accounting vouchers.");
        await license.EnsureCanTransactAsync(ct);
        var settings = await settingsService.GetAsync(ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);

        await using var tx = await db.BeginAsync(IsolationLevel.ReadCommitted, ct);

        TradeDocument doc;
        if (id is null)
        {
            doc = new TradeDocument { VoucherType = dto.VoucherType, UserId = user.UserId, Counter = user.Machine };
            db.TradeDocuments.Add(doc);
        }
        else
        {
            doc = await db.TradeDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == id, ct)
                  ?? throw new NotFoundException("Document not found.");
            if (doc.VoucherType != dto.VoucherType) throw new DomainException("Voucher type cannot change on edit.");
            if (doc.Cancelled) throw new DomainException("A cancelled document cannot be edited.", "cancelled");
            if (doc.IsProcessed) throw new DomainException("This order has already been converted to a bill.", "processed");
            if (!string.IsNullOrEmpty(dto.RowVersion) && !Convert.FromBase64String(dto.RowVersion).SequenceEqual(doc.RowVersion))
                throw new ConflictException("This document was changed by another user. Reload and try again.");
            var oldFy = await db.FinancialYears.FirstAsync(f => f.Id == doc.FinancialYearId, ct);
            if (oldFy.IsClosed) throw new DomainException("The document belongs to a closed financial year.", "year_closed");
            await ReverseAsync(doc, ct);
            db.TradeDocumentLines.RemoveRange(doc.Lines);
            doc.Lines.Clear();
            doc.Version++;
        }

        var fy = await years.ForPostingAsync(dto.Date, ct);
        if (id is not null && fy.Id != doc.FinancialYearId) throw new DomainException("An edit cannot move a document into another financial year.");
        doc.FinancialYearId = fy.Id;
        await ApplyHeaderAsync(doc, dto, company, settings, ct);

        var warnings = new List<string>();
        await BuildLinesAsync(doc, dto, settings, ct);
        await ApplyLoyaltyRedemptionAsync(doc, dto, settings, ct);
        if (doc.VoucherType == VoucherType.Production) SpreadProductionCost(doc);

        DocumentCalculator.Calculate(doc, new CalcOptions
        {
            ServiceTaxRate = settings.ServiceTaxRate,
            ServiceCessRate = settings.ServiceCessRate,
            TaxClaimable = settings.InputTaxClaimable && doc.TaxType != TaxType.NoTax,
        });
        if (doc.VoucherType == VoucherType.Production)
            doc.Cost = DocumentCalculator.R2(doc.Lines.Where(l => l.Direction == StockDirection.In).Sum(l => l.Cost * l.BaseQty));
        ResolvePayment(doc, dto);
        await ValidateAsync(doc, settings, warnings, ct);
        if (warnings.Count > 0 && !dto.AcknowledgeWarnings) throw new WarningException(warnings);

        if (id is null)
        {
            var (seq, no) = await numbering.NextAsync(doc.VoucherType, fy.Id, ct);
            doc.SequenceNo = seq;
            doc.DocumentNo = no;
        }
        await db.SaveChangesAsync(ct);

        await PostAsync(doc, settings, ct);
        await UpdateMastersAsync(doc, settings, ct);
        if (doc.SourceOrderId is { } orderId)
            await db.TradeDocuments.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsProcessed, true), ct);

        audit.Log(Screens.ForVoucher(doc.VoucherType), id is null ? "Add" : "Edit",
            $"{doc.VoucherType.DisplayName()} {doc.DocumentNo} amount {doc.BillAmount:0.00}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await notifier.DocumentSavedAsync(user.CompanyId, new
        {
            doc.Id, doc.DocumentNo, VoucherType = doc.VoucherType.ToString(), doc.BillAmount, doc.PaymentMode, doc.CashAmount,
            doc.CardAmount, doc.CreditAmount, doc.Counter, Action = id is null ? "Add" : "Edit",
        });
        if (doc.VoucherType.PostsStock())
            await notifier.StockChangedAsync(user.CompanyId, doc.Lines.Select(l => l.ProductId).Distinct());
        if (id is null && doc.VoucherType.IsSalesFamily())
            await messaging.QueueInvoiceSmsAsync(doc.Id, settings, ct);

        var result = await GetAsync(doc.Id, ct);
        result.Warnings = warnings;
        return result;
    }

    private async Task ApplyHeaderAsync(TradeDocument doc, TradeDocumentDto dto, Company company, CompanySettings settings, CancellationToken ct)
    {
        doc.Date = dto.Date;
        doc.PaymentMode = dto.PaymentMode;
        doc.SalespersonId = dto.SalespersonId;
        doc.PriceListId = dto.PriceListId;
        doc.Remarks = dto.Remarks?.Trim();
        doc.ReferenceNo = dto.ReferenceNo?.Trim();
        doc.ReferenceDate = dto.ReferenceDate;
        doc.OrderDetails = dto.OrderDetails;
        doc.DespatchDetails = dto.DespatchDetails;
        doc.DeliveryDate = dto.DeliveryDate;
        doc.SourceOrderId = dto.SourceOrderId;
        doc.BillDiscountPercent = dto.BillDiscountPercent;
        doc.BillDiscount = dto.BillDiscountPercent > 0 ? 0 : dto.BillDiscount;
        doc.OtherExpense = dto.OtherExpense;
        doc.OtherExpenseTaxRate = dto.OtherExpenseTaxRate;
        doc.Freight = dto.Freight;
        doc.FreightTaxRate = dto.FreightTaxRate;
        doc.RoundOffMode = dto.RoundOffMode ?? settings.DefaultRoundOff;
        doc.CashDiscountPercent = dto.CashDiscountPercent;
        doc.CashDiscount = dto.CashDiscountPercent > 0 ? 0 : dto.CashDiscount;
        doc.CostFactor = dto.CostFactor <= 0 ? 1 : dto.CostFactor;
        doc.BomId = dto.BomId;
        doc.LabourCost = dto.LabourCost;
        doc.OtherCost = dto.OtherCost;
        doc.CardLedgerId = dto.CardLedgerId;
        doc.PaymentReference = dto.PaymentReference?.Trim();
        doc.TenderedAmount = dto.TenderedAmount;

        doc.PartyLedgerId = dto.PartyLedgerId;
        Ledger? party = null;
        if (dto.PartyLedgerId is { } pid)
        {
            party = await db.Ledgers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == pid, ct)
                    ?? throw new DomainException("Party ledger not found.");
            if (!party.IsActive) throw new DomainException($"Ledger {party.Name} is inactive.");
        }
        doc.PartyName = FirstNonEmpty(dto.PartyName, party?.PrintName, party?.Name);
        doc.PartyAddress = FirstNonEmpty(dto.PartyAddress, party?.Address);
        doc.PartyPhone = FirstNonEmpty(dto.PartyPhone, party?.Mobile, party?.Phone);
        var gstin = FirstNonEmpty(dto.PartyGstin, party?.Gstin);
        if (gstin is not null) Domain.Validation.Gstin.EnsureValid(gstin, "Party GSTIN");
        doc.PartyGstin = gstin is null ? null : Domain.Validation.Gstin.Normalize(gstin);
        doc.PartyStateCode = FirstNonEmpty(dto.PartyStateCode, party?.StateCode, Domain.Validation.Gstin.StateCode(doc.PartyGstin));

        doc.BillType = dto.BillType ?? (string.IsNullOrEmpty(doc.PartyGstin) ? BillType.B2C : BillType.B2B);
        doc.TaxType = dto.TaxType ?? (!string.IsNullOrEmpty(doc.PartyStateCode) && !string.IsNullOrEmpty(company.StateCode)
                                      && doc.PartyStateCode != company.StateCode ? TaxType.InterState : TaxType.IntraState);

        if (doc.VoucherType.IsSalesFamily() && settings.EnableLoyalty && party?.LoyaltySchemeId is not null)
            doc.LoyaltySchemeId = party.LoyaltySchemeId;
        else doc.LoyaltySchemeId = null;

        if (!string.IsNullOrWhiteSpace(dto.CardLast4))
        {
            var digits = dto.CardLast4.Trim();
            // Only the last four digits are ever accepted; a full card number is refused, never stored.
            if (digits.Length != 4 || !digits.All(char.IsDigit))
                throw new DomainException("Enter only the last 4 digits of the card.", "card_data");
            doc.CardLast4 = digits;
        }
        else doc.CardLast4 = null;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private async Task BuildLinesAsync(TradeDocument doc, TradeDocumentDto dto, CompanySettings settings, CancellationToken ct)
    {
        var inputLines = dto.Lines.Where(l => l.ProductId > 0 && (l.Qty != 0 || l.FreeQty != 0)).ToList();
        if (inputLines.Count == 0) throw new DomainException("Add at least one item.", "no_items");

        var productIds = inputLines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var batchIds = inputLines.Where(l => l.BatchId is > 0).Select(l => l.BatchId!.Value).Distinct().ToList();
        var batches = await db.Batches.Where(b => batchIds.Contains(b.Id) || productIds.Contains(b.ProductId)).ToListAsync(ct);
        var docDate = clock.BusinessDate(doc.Date);
        bool purchaseSide = doc.VoucherType.IsPurchaseSide();
        bool postsStock = doc.VoucherType.PostsStock();
        int n = 1;

        foreach (var li in inputLines)
        {
            if (!products.TryGetValue(li.ProductId, out var p)) throw new DomainException($"Product {li.ProductId} not found.");
            if (!p.IsActive) throw new DomainException($"Product {p.Name} is inactive.");
            if (li.Qty < 0 || li.FreeQty < 0) throw new DomainException($"{p.Name}: quantity cannot be negative.");
            if (li.Rate < 0) throw new DomainException($"{p.Name}: rate cannot be negative.");

            var line = new TradeDocumentLine
            {
                LineNo = n++, ProductId = p.Id, ItemClass = p.ItemClass, HsnCode = p.HsnCode,
                Qty = li.Qty, FreeQty = li.FreeQty, Rate = li.Rate, Mrp = li.Mrp,
                DiscountPercent = li.DiscountPercent, DiscountAmount = li.DiscountPercent > 0 ? 0 : li.DiscountAmount,
                SaleRate = li.SaleRate, ExpiryDate = li.ExpiryDate, ExtraColumn = li.ExtraColumn, PatternNo = li.PatternNo,
                Remarks = li.Remarks, Direction = li.Direction,
            };

            if (li.IsAltUnit)
            {
                if (!settings.EnableUnitConversion) throw new DomainException("Unit conversion is disabled in settings.");
                if (string.IsNullOrWhiteSpace(p.AltUnit) || p.ConversionFactor <= 0)
                    throw new DomainException($"{p.Name} has no alternate unit.", "alt_unit");
                line.IsAltUnit = true;
                line.Unit = p.AltUnit;
                line.UnitFactor = p.ConversionFactor;
            }
            else
            {
                line.Unit = p.MainUnit;
                line.UnitFactor = 1;
            }

            ApplyTaxRates(line, p, doc, settings, docDate);
            line.TaxInclusive = li.TaxInclusive ?? (purchaseSide ? p.PurchaseTaxInclusive : p.SaleTaxInclusive);

            if (doc.VoucherType is VoucherType.Production or VoucherType.StockAdjustment && line.Direction == StockDirection.None)
                throw new DomainException($"{p.Name}: choose stock in or out.", "direction_required");
            if (doc.VoucherType.IsTaxFree())
            {
                line.CgstRate = line.SgstRate = line.IgstRate = line.CessRate = line.AddCessRate = line.KfCessRate = 0;
                line.TaxInclusive = false;
            }

            if (postsStock)
                ResolveBatch(doc, line, li, p, batches, settings, purchaseSide);
            else if (li.BatchId is > 0 && batches.FirstOrDefault(b => b.Id == li.BatchId && b.ProductId == p.Id) is { } ob)
            {
                // Orders only reference an existing batch; they never create one.
                line.BatchId = ob.Id;
                line.BatchCode = ob.Code;
            }
            else line.BatchCode = li.BatchCode;

            if (p.ItemClass == ItemClass.Goods && settings.EnableExpiry && p.TrackExpiry && IsStockIn(doc, line) && purchaseSide && line.ExpiryDate is null)
                throw new DomainException($"{p.Name}: expiry date is required.", "expiry_required");

            doc.Lines.Add(line);
        }
    }

    private static bool IsStockIn(TradeDocument doc, TradeDocumentLine l) =>
        (l.Direction != StockDirection.None ? l.Direction : doc.VoucherType.DefaultDirection()) == StockDirection.In;

    /// <summary>GST rates always come from the product master (secondary slab above the threshold); cess per the settings.</summary>
    private static void ApplyTaxRates(TradeDocumentLine line, Product p, TradeDocument doc, CompanySettings s, DateOnly date)
    {
        bool secondary = p.HasSecondaryTax && p.SecondaryTaxThreshold > 0 && line.UnitFactor > 0
                         && line.Rate / line.UnitFactor > p.SecondaryTaxThreshold;
        line.CgstRate = secondary ? p.Cgst2Rate : p.CgstRate;
        line.SgstRate = secondary ? p.Sgst2Rate : p.SgstRate;
        line.IgstRate = secondary ? p.Igst2Rate : p.IgstRate;
        line.CessRate = s.EnableCess ? p.CessRate : 0;
        line.AddCessRate = s.EnableAddCess ? p.AddCessPerUnit * line.UnitFactor : 0;
        // Kerala flood cess: intra-state B2C supplies inside the configured window only; not creditable on purchases.
        line.KfCessRate = !doc.VoucherType.IsPurchaseSide() && doc.BillType == BillType.B2C && doc.TaxType == TaxType.IntraState
                          && s.KfCessAppliesOn(date) ? p.KfCessRate : 0;
    }

    private void ResolveBatch(TradeDocument doc, TradeDocumentLine line, TradeLineDto li, Product p, List<Batch> batches,
        CompanySettings s, bool purchaseSide)
    {
        if (p.ItemClass == ItemClass.Service) { line.BatchId = null; line.BatchCode = null; return; }
        decimal factor = line.UnitFactor;
        decimal mrpBase = DocumentCalculator.R4(line.Mrp / factor);
        bool stockIn = IsStockIn(doc, line);
        bool createsBatches = stockIn && (purchaseSide || doc.VoucherType == VoucherType.Production);

        Batch? batch = null;
        if (li.BatchId is > 0)
        {
            batch = batches.FirstOrDefault(b => b.Id == li.BatchId && b.ProductId == p.Id)
                    ?? throw new DomainException($"{p.Name}: batch not found.");
            if (createsBatches && s.EnableMultiMrp && mrpBase > 0 && batch.Mrp != mrpBase)
                batch = FindOrCreate(batch.Code);
        }
        else
        {
            string code = !string.IsNullOrWhiteSpace(li.BatchCode) ? li.BatchCode.Trim()
                : !s.EnableBatch ? DefaultCode(p)
                : createsBatches ? $"{s.AutoBatchPrefix}{p.Id}{clock.Now:yyMMddHHmmss}{line.LineNo}"
                : DefaultCode(p);
            if (!s.EnableBatch) code = DefaultCode(p);
            var existing = batches.Where(b => b.ProductId == p.Id && b.Code == code).ToList();
            batch = createsBatches
                ? FindOrCreate(code)
                : existing.FirstOrDefault(b => mrpBase == 0 || b.Mrp == mrpBase) ?? existing.FirstOrDefault()
                  ?? (batches.Count(b => b.ProductId == p.Id && b.IsActive) == 1 && string.IsNullOrWhiteSpace(li.BatchCode)
                      ? batches.First(b => b.ProductId == p.Id && b.IsActive) : null)
                  ?? FindOrCreate(code);
        }

        line.BatchCode = batch.Code;
        // A batch created in this save gets its id when the document is saved (EF fixes up the FK).
        line.Batch = batch;
        if (batch.Id > 0) line.BatchId = batch.Id;
        if (line.Mrp == 0) line.Mrp = batch.Mrp * factor;
        if (!purchaseSide || doc.VoucherType == VoucherType.PurchaseReturn)
            line.Cost = batch.Cost > 0 ? batch.Cost : batch.PurchaseRate > 0 ? batch.PurchaseRate : p.PurchaseRate;
        if (doc.VoucherType == VoucherType.OpeningStock || (doc.VoucherType == VoucherType.StockAdjustment && stockIn))
            line.Cost = line.Rate > 0 ? DocumentCalculator.R4(line.Rate / factor) : line.Cost;
        if (doc.VoucherType == VoucherType.Production && !stockIn)
            line.Cost = batch.Cost > 0 ? batch.Cost : p.PurchaseRate;

        Batch FindOrCreate(string code)
        {
            var match = batches.FirstOrDefault(b => b.ProductId == p.Id && b.Code == code && (!s.EnableMultiMrp || b.Mrp == mrpBase));
            if (match is not null) return match;
            var nb = new Batch
            {
                ProductId = p.Id, Code = code, Mrp = mrpBase > 0 ? mrpBase : p.Mrp,
                PurchaseRate = purchaseSide ? DocumentCalculator.R4(line.Rate / factor) : p.PurchaseRate,
                SaleRate = line.SaleRate > 0 ? DocumentCalculator.R4(line.SaleRate / factor) : p.SaleRate,
                Cost = p.PurchaseRate, ExpiryDate = line.ExpiryDate, InternalNo = clock.Now.ToUnixTimeMilliseconds(),
            };
            db.Batches.Add(nb);
            batches.Add(nb);
            return nb;
        }
    }

    private static string DefaultCode(Product p) => string.IsNullOrWhiteSpace(p.DefaultBarcode) ? p.Code : p.DefaultBarcode;

    private async Task ApplyLoyaltyRedemptionAsync(TradeDocument doc, TradeDocumentDto dto, CompanySettings s, CancellationToken ct)
    {
        doc.PointsRedeemed = 0;
        doc.RedeemAmount = 0;
        if (dto.PointsRedeemed <= 0) return;
        if (doc.LoyaltySchemeId is null || doc.PartyLedgerId is null) throw new DomainException("This customer has no loyalty scheme.");
        var scheme = await db.LoyaltySchemes.AsNoTracking().FirstAsync(x => x.Id == doc.LoyaltySchemeId, ct);
        decimal balance = await ledgers.LoyaltyBalanceAsync(doc.PartyLedgerId.Value, doc.FinancialYearId, ct);
        if (balance < scheme.TargetPoints) throw new DomainException($"Points balance {balance:0.##} has not reached the target {scheme.TargetPoints:0.##}.", "loyalty_target");
        if (dto.PointsRedeemed > balance) throw new DomainException($"Only {balance:0.##} points are available.", "loyalty_balance");
        doc.PointsRedeemed = dto.PointsRedeemed;
        doc.RedeemAmount = DocumentCalculator.R2(dto.PointsRedeemed * scheme.PointValue);
    }

    private static void SpreadProductionCost(TradeDocument doc) => DocumentCalculator.SpreadProductionCost(doc);

    private static void ResolvePayment(TradeDocument doc, TradeDocumentDto dto)
    {
        if (!doc.VoucherType.PostsAccounts())
        {
            doc.CashAmount = doc.CardAmount = doc.CreditAmount = 0;
            return;
        }
        switch (doc.PaymentMode)
        {
            case PaymentMode.Cash:
                doc.CashAmount = doc.BillAmount; doc.CardAmount = 0; doc.CreditAmount = 0; break;
            case PaymentMode.Credit:
                doc.CashAmount = 0; doc.CardAmount = 0; doc.CreditAmount = doc.BillAmount; break;
            default:
                doc.CashAmount = dto.CashAmount; doc.CardAmount = dto.CardAmount;
                doc.CreditAmount = doc.VoucherType.IsPurchaseFamily()
                    ? doc.BillAmount - dto.CashAmount - dto.CardAmount
                    : dto.CreditAmount;
                if (doc.CashAmount < 0 || doc.CardAmount < 0 || doc.CreditAmount < 0)
                    throw new DomainException("Payment amounts cannot be negative.", "payment_split");
                break;
        }
    }

    private async Task ValidateAsync(TradeDocument doc, CompanySettings s, List<string> warnings, CancellationToken ct)
    {
        var t = doc.VoucherType;
        if (t.IsSalesFamily() && s.RequireSalesperson && doc.SalespersonId is null)
            throw new DomainException("Select a salesperson.", "salesperson_required");
        if (doc.SalespersonId is { } spId && !await db.Employees.AnyAsync(e => e.Id == spId && e.IsActive, ct))
            throw new DomainException("Salesperson not found.");

        Ledger? party = doc.PartyLedgerId is null ? null : await db.Ledgers.AsNoTracking().FirstAsync(l => l.Id == doc.PartyLedgerId, ct);
        bool needsParty = t.PostsAccounts() && (doc.PaymentMode == PaymentMode.Credit || doc.CreditAmount > 0);
        if (needsParty && (party is null || party.SystemCode == SystemLedger.WalkInCustomer))
            throw new DomainException(t.IsPurchaseSide() ? "Select a supplier for a credit purchase." : "Select a customer for a credit bill (not the walk-in customer).", "party_required");
        if (t.IsPurchaseFamily() && party is null && doc.PaymentMode != PaymentMode.Cash)
            throw new DomainException("Select a supplier.", "party_required");

        if (t.IsSalesFamily() && party is not null && party.CreditLimit > 0 && doc.CreditAmount > 0 && s.CreditLimitMode != CreditLimitMode.Ignore)
        {
            // On edit the old version's entries were already reversed, so the balance excludes it.
            decimal balance = await ledgers.BalanceAsync(party.Id, doc.FinancialYearId, ct);
            decimal after = balance + doc.CreditAmount;
            if (after > party.CreditLimit)
            {
                string msg = $"{party.Name}: balance after this bill ({after:0.00}) exceeds the credit limit ({party.CreditLimit:0.00}).";
                if (s.CreditLimitMode == CreditLimitMode.Block) throw new DomainException(msg, "credit_limit");
                warnings.Add(msg);
            }
        }

        if (s.NegativeStockMode != NegativeStockMode.Allow && t.PostsStock())
        {
            var outLines = doc.Lines.Where(l => l.ItemClass == ItemClass.Goods && l.Batch is not null && !IsStockIn(doc, l)).ToList();
            var ids = outLines.Select(l => l.Batch!.Id).Where(i => i > 0).Distinct().ToList();
            if (ids.Count > 0 && s.NegativeStockMode == NegativeStockMode.Block)
            {
                // Lock the batch rows so two counters can't both sell the last unit.
                var idList = string.Join(",", ids);
                await db.Database.ExecuteSqlAsync($"SELECT Id FROM Batches WITH (UPDLOCK, ROWLOCK) WHERE Id IN (SELECT CAST(value AS bigint) FROM STRING_SPLIT({idList}, ','))", ct);
            }
            var onHand = await stock.OnHandByBatchAsync(ids, doc.FinancialYearId, ct);
            foreach (var g in outLines.GroupBy(l => l.Batch!))
            {
                decimal need = g.Sum(l => l.BaseQty);
                decimal have = g.Key.Id > 0 ? onHand.GetValueOrDefault(g.Key.Id) : 0;
                if (have - need < 0)
                {
                    var name = await db.Products.Where(p => p.Id == g.First().ProductId).Select(p => p.Name).FirstAsync(ct);
                    string msg = $"{name} ({g.Key.Code}): stock {have:0.###}, required {need:0.###}.";
                    if (s.NegativeStockMode == NegativeStockMode.Block) throw new DomainException("Insufficient stock. " + msg, "negative_stock");
                    warnings.Add("Negative stock: " + msg);
                }
            }
        }
    }

    private async Task PostAsync(TradeDocument doc, CompanySettings s, CancellationToken ct)
    {
        var result = PostingEngine.PostTrade(doc, new PostingContext
        {
            Ledger = await systemLedgers.GetAsync(ct),
            InputTaxClaimable = s.InputTaxClaimable && doc.TaxType != TaxType.NoTax,
        });

        foreach (var leg in result.Legs)
            db.AccountEntries.Add(new AccountEntry
            {
                FinancialYearId = doc.FinancialYearId, VoucherType = doc.VoucherType, TradeDocumentId = doc.Id,
                DocumentNo = doc.DocumentNo, Date = doc.Date, LedgerId = leg.LedgerId, ContraLedgerId = leg.ContraLedgerId,
                Debit = leg.Debit, Credit = leg.Credit, Remarks = leg.Remarks,
            });
        foreach (var sl in result.Stock)
            db.StockMovements.Add(new StockMovement
            {
                FinancialYearId = doc.FinancialYearId, VoucherType = doc.VoucherType, TradeDocumentId = doc.Id,
                DocumentNo = doc.DocumentNo, Date = doc.Date, ProductId = sl.ProductId, BatchId = sl.BatchId,
                QtyIn = sl.QtyIn, QtyOut = sl.QtyOut, Mrp = sl.Mrp, SaleRate = sl.SaleRate, Cost = sl.Cost,
                LedgerId = doc.PartyLedgerId,
            });

        // Bill-by-bill outstanding for the credit part of a party bill.
        if (s.EnableBillByBill && doc.PartyLedgerId is not null && doc.VoucherType.PostsAccounts())
        {
            bool purchase = doc.VoucherType.IsPurchaseSide();
            bool isReturn = doc.VoucherType is VoucherType.SalesReturn or VoucherType.PurchaseReturn;
            decimal amount = isReturn ? (doc.PaymentMode == PaymentMode.Cash ? 0 : doc.BillAmount) : doc.CreditAmount;
            if (amount > 0)
            {
                var party = await db.Ledgers.AsNoTracking().FirstAsync(l => l.Id == doc.PartyLedgerId, ct);
                db.BillOutstandings.Add(new BillOutstanding
                {
                    LedgerId = doc.PartyLedgerId.Value, VoucherType = doc.VoucherType, TradeDocumentId = doc.Id,
                    DocumentNo = doc.DocumentNo, Date = doc.Date, Amount = amount,
                    Side = (purchase ^ isReturn) ? EntrySide.Cr : EntrySide.Dr,
                    DueDate = party.CreditDays > 0 ? DateOnly.FromDateTime(doc.Date.Date).AddDays(party.CreditDays) : null,
                });
            }
        }

        // Loyalty earned on the bill and redeemed against it, keyed to this document.
        if (doc.LoyaltySchemeId is not null && doc.PartyLedgerId is not null && doc.VoucherType.IsSalesFamily())
        {
            var scheme = await db.LoyaltySchemes.AsNoTracking().FirstAsync(x => x.Id == doc.LoyaltySchemeId, ct);
            doc.PointsEarned = DocumentCalculator.LoyaltyPoints(doc.BillAmount, scheme);
            if (doc.PointsEarned > 0 || doc.PointsRedeemed > 0)
                db.LoyaltyTransactions.Add(new LoyaltyTransaction
                {
                    FinancialYearId = doc.FinancialYearId, LedgerId = doc.PartyLedgerId.Value, SchemeId = scheme.Id,
                    VoucherType = doc.VoucherType, TradeDocumentId = doc.Id, DocumentNo = doc.DocumentNo, Date = doc.Date,
                    SaleAmount = doc.BillAmount, PointsIn = doc.PointsEarned, PointsOut = doc.PointsRedeemed,
                });
        }
    }

    /// <summary>Optional master updates on stock-in: batch rates, product rates, default barcode.</summary>
    private async Task UpdateMastersAsync(TradeDocument doc, CompanySettings s, CancellationToken ct)
    {
        bool purchase = doc.VoucherType.IsPurchaseFamily() || doc.VoucherType is VoucherType.OpeningStock or VoucherType.ReceiptNote;
        bool production = doc.VoucherType == VoucherType.Production;
        if (!purchase && !production) return;

        var inLines = doc.Lines.Where(l => l.ItemClass == ItemClass.Goods && IsStockIn(doc, l) && l.BatchId is > 0).ToList();
        var batchIds = inLines.Select(l => l.BatchId!.Value).ToList();
        var batches = await db.Batches.Where(b => batchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var products = await db.Products.Where(p => inLines.Select(l => l.ProductId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        foreach (var l in inLines)
        {
            var b = batches[l.BatchId!.Value];
            decimal f = l.UnitFactor;
            b.Cost = l.Cost;
            if (purchase) b.PurchaseRate = DocumentCalculator.R4(l.Rate / f);
            if (l.SaleRate > 0) b.SaleRate = DocumentCalculator.R4(l.SaleRate / f);
            if (l.Mrp > 0 && !s.EnableMultiMrp) b.Mrp = DocumentCalculator.R4(l.Mrp / f);
            if (l.ExpiryDate is not null) b.ExpiryDate = l.ExpiryDate;
            b.IsActive = true;

            var p = products[l.ProductId];
            if ((purchase && s.UpdateRatesOnPurchase) || (production && s.UpdateRatesOnProduction))
            {
                p.PurchaseRate = purchase ? DocumentCalculator.R4(l.Rate / f) : l.Cost;
                if (l.SaleRate > 0) p.SaleRate = DocumentCalculator.R4(l.SaleRate / f);
                if (l.Mrp > 0) p.Mrp = DocumentCalculator.R4(l.Mrp / f);
            }
            if (purchase && s.UpdateDefaultBarcode && s.EnableBatch && p.DefaultBarcode != b.Code
                && !await db.Products.AnyAsync(x => x.DefaultBarcode == b.Code && x.Id != p.Id, ct))
                p.DefaultBarcode = b.Code;
        }
    }

    /// <summary>Removes a document's account, stock, ageing and loyalty effects (used by edit and cancel).</summary>
    private async Task ReverseAsync(TradeDocument doc, CancellationToken ct)
    {
        var bills = await db.BillOutstandings.Include(b => b.Settlements).Where(b => b.TradeDocumentId == doc.Id).ToListAsync(ct);
        if (bills.Any(b => b.Settlements.Count > 0))
            throw new DomainException("Payments have been settled against this bill. Remove the settlements first.", "bill_settled");
        db.BillOutstandings.RemoveRange(bills);
        await db.AccountEntries.Where(e => e.TradeDocumentId == doc.Id).ExecuteDeleteAsync(ct);
        await db.StockMovements.Where(m => m.TradeDocumentId == doc.Id).ExecuteDeleteAsync(ct);
        await db.LoyaltyTransactions.Where(t => t.TradeDocumentId == doc.Id).ExecuteDeleteAsync(ct);
        await db.BillSettlements.Where(x => x.TradeDocumentId == doc.Id).ExecuteDeleteAsync(ct);
    }

    public async Task CancelAsync(long id, string? reason, CancellationToken ct)
    {
        await using var tx = await db.BeginAsync(IsolationLevel.ReadCommitted, ct);
        var doc = await db.TradeDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Document not found.");
        if (doc.Cancelled) throw new DomainException("Already cancelled.");
        var fy = await db.FinancialYears.FirstAsync(f => f.Id == doc.FinancialYearId, ct);
        if (fy.IsClosed) throw new DomainException("The document belongs to a closed financial year.", "year_closed");
        if (doc.IsProcessed) throw new DomainException("This order has been converted to a bill; cancel the bill first.");

        await ReverseAsync(doc, ct);
        doc.Cancelled = true;
        doc.CancelledAt = clock.Now;
        doc.CancelledBy = user.UserId;
        doc.CancelReason = reason;
        if (doc.SourceOrderId is { } orderId)
            await db.TradeDocuments.Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsProcessed, false), ct);
        audit.Log(Screens.ForVoucher(doc.VoucherType), "Cancel", $"{doc.VoucherType.DisplayName()} {doc.DocumentNo} {reason}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.DocumentSavedAsync(user.CompanyId, new { doc.Id, doc.DocumentNo, VoucherType = doc.VoucherType.ToString(), Action = "Cancel" });
    }

    /// <summary>Only unposted documents (orders, estimates) can be deleted outright; posted ones are cancelled.</summary>
    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var doc = await db.TradeDocuments.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Document not found.");
        if (!doc.VoucherType.IsOrder()) throw new DomainException("Posted documents cannot be deleted. Cancel instead.", "delete_posted");
        if (doc.IsProcessed) throw new DomainException("This order has been converted to a bill.");
        if (await db.TradeDocuments.AnyAsync(d => d.SourceOrderId == id, ct)) throw new DomainException("A bill references this order.");
        db.TradeDocuments.Remove(doc);
        audit.Log(Screens.ForVoucher(doc.VoucherType), "Delete", $"{doc.VoucherType.DisplayName()} {doc.DocumentNo}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Server-authoritative calculation without saving (used by the POS/invoice screens for totals).</summary>
    public async Task<TradeDocumentDto> PreviewAsync(TradeDocumentDto dto, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, ct);
        var doc = new TradeDocument { VoucherType = dto.VoucherType, FinancialYearId = user.FinancialYearId };
        await ApplyHeaderAsync(doc, dto, company, settings, ct);
        await BuildLinesAsync(doc, dto, settings, ct);
        DocumentCalculator.Calculate(doc, new CalcOptions { ServiceTaxRate = settings.ServiceTaxRate, ServiceCessRate = settings.ServiceCessRate,
            TaxClaimable = settings.InputTaxClaimable && doc.TaxType != TaxType.NoTax });
        // Nothing may persist from a preview (pending batches included).
        foreach (var e in ((DbContext)db).ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
        return Map(doc, new Dictionary<long, (string Code, string Name, string? Regional)>(), null);
    }

    public async Task<TradeDocumentDto> GetAsync(long id, CancellationToken ct)
    {
        var doc = await db.TradeDocuments.AsNoTracking().Include(d => d.Lines).Include(d => d.Salesperson)
                      .FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Document not found.");
        var pids = doc.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id))
            .Select(p => new { p.Id, p.Code, p.Name, p.RegionalName }).ToDictionaryAsync(p => p.Id, ct);
        string? userName = doc.UserId is null ? null : await db.Users.Where(u => u.Id == doc.UserId).Select(u => u.UserName).FirstOrDefaultAsync(ct);
        var dto = Map(doc, products.ToDictionary(k => k.Key, v => (v.Value.Code, v.Value.Name, v.Value.RegionalName)), userName);
        return dto;
    }

    public static TradeDocumentDto Map(TradeDocument d, IReadOnlyDictionary<long, (string Code, string Name, string? Regional)> products, string? userName) => new()
    {
        Id = d.Id, VoucherType = d.VoucherType, DocumentNo = d.DocumentNo, Date = d.Date, Version = d.Version,
        RowVersion = d.RowVersion.Length > 0 ? Convert.ToBase64String(d.RowVersion) : null,
        PartyLedgerId = d.PartyLedgerId, PartyName = d.PartyName, PartyAddress = d.PartyAddress, PartyPhone = d.PartyPhone,
        PartyGstin = d.PartyGstin, PartyStateCode = d.PartyStateCode, PaymentMode = d.PaymentMode, TaxType = d.TaxType,
        BillType = d.BillType, SalespersonId = d.SalespersonId, SalespersonName = d.Salesperson?.Name, PriceListId = d.PriceListId,
        Remarks = d.Remarks, ReferenceNo = d.ReferenceNo, ReferenceDate = d.ReferenceDate, OrderDetails = d.OrderDetails,
        DespatchDetails = d.DespatchDetails, DeliveryDate = d.DeliveryDate, SourceOrderId = d.SourceOrderId,
        BillDiscountPercent = d.BillDiscountPercent, BillDiscount = d.BillDiscount, OtherExpense = d.OtherExpense,
        OtherExpenseTaxRate = d.OtherExpenseTaxRate, Freight = d.Freight, FreightTaxRate = d.FreightTaxRate,
        RoundOffMode = d.RoundOffMode, CashDiscountPercent = d.CashDiscountPercent, CashDiscount = d.CashDiscount,
        CostFactor = d.CostFactor, CashAmount = d.CashAmount, CardAmount = d.CardAmount, CreditAmount = d.CreditAmount,
        TenderedAmount = d.TenderedAmount, CardLedgerId = d.CardLedgerId, CardLast4 = d.CardLast4, PaymentReference = d.PaymentReference,
        PointsRedeemed = d.PointsRedeemed, BomId = d.BomId, LabourCost = d.LabourCost, OtherCost = d.OtherCost,
        GrossAmount = d.GrossAmount, ItemDiscount = d.ItemDiscount, TaxableAmount = d.TaxableAmount, ExemptAmount = d.ExemptAmount,
        CgstAmount = d.CgstAmount, SgstAmount = d.SgstAmount, IgstAmount = d.IgstAmount, CessAmount = d.CessAmount,
        AddCessAmount = d.AddCessAmount, KfCessAmount = d.KfCessAmount, ServiceTaxAmount = d.ServiceTaxAmount,
        ServiceCessAmount = d.ServiceCessAmount, OtherExpenseTax = d.OtherExpenseTax, FreightTax = d.FreightTax,
        RoundOff = d.RoundOff, BillAmount = d.BillAmount, TotalMrp = d.TotalMrp, Cost = d.Cost, Profit = d.Profit,
        PointsEarned = d.PointsEarned, RedeemAmount = d.RedeemAmount, Cancelled = d.Cancelled, CancelReason = d.CancelReason,
        IsProcessed = d.IsProcessed, Counter = d.Counter, UserName = userName, CreatedAt = d.CreatedAt,
        Lines = d.Lines.OrderBy(l => l.LineNo).Select(l => new TradeLineDto
        {
            Id = l.Id, LineNo = l.LineNo, ProductId = l.ProductId,
            ProductCode = products.TryGetValue(l.ProductId, out var p) ? p.Code : null,
            ProductName = products.TryGetValue(l.ProductId, out var p2) ? p2.Name : null,
            RegionalName = products.TryGetValue(l.ProductId, out var p3) ? p3.Regional : null,
            BatchId = l.BatchId, BatchCode = l.BatchCode, Direction = l.Direction, ItemClass = l.ItemClass, HsnCode = l.HsnCode,
            Unit = l.Unit, UnitFactor = l.UnitFactor, IsAltUnit = l.IsAltUnit, Qty = l.Qty, FreeQty = l.FreeQty, Mrp = l.Mrp,
            Rate = l.Rate, TaxInclusive = l.TaxInclusive, Gross = l.Gross, DiscountPercent = l.DiscountPercent,
            DiscountAmount = l.DiscountAmount, BillDiscountShare = l.BillDiscountShare, Taxable = l.Taxable,
            CgstRate = l.CgstRate, CgstAmount = l.CgstAmount, SgstRate = l.SgstRate, SgstAmount = l.SgstAmount,
            IgstRate = l.IgstRate, IgstAmount = l.IgstAmount, CessRate = l.CessRate, CessAmount = l.CessAmount,
            AddCessRate = l.AddCessRate, AddCessAmount = l.AddCessAmount, KfCessRate = l.KfCessRate, KfCessAmount = l.KfCessAmount,
            LineTotal = l.LineTotal, SaleRate = l.SaleRate, ExpiryDate = l.ExpiryDate, Cost = l.Cost, Profit = l.Profit,
            ProfitPercent = l.ProfitPercent, ExtraColumn = l.ExtraColumn, PatternNo = l.PatternNo, Remarks = l.Remarks,
        }).ToList(),
    };

    public async Task<PagedResult<DocumentListItem>> ListAsync(DocumentQuery q, CancellationToken ct)
    {
        var query = db.TradeDocuments.AsNoTracking().AsQueryable();
        if (q.Type is not null) query = query.Where(d => d.VoucherType == q.Type);
        if (q.From is not null) { var f = clock.DayStart(q.From.Value); query = query.Where(d => d.Date >= f); }
        if (q.To is not null) { var t = clock.DayEnd(q.To.Value); query = query.Where(d => d.Date < t); }
        if (q.PartyId is not null) query = query.Where(d => d.PartyLedgerId == q.PartyId);
        if (q.Cancelled is not null) query = query.Where(d => d.Cancelled == q.Cancelled);
        if (q.OpenOrdersOnly == true) query = query.Where(d => !d.IsProcessed && !d.Cancelled);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(d => d.DocumentNo.Contains(s) || (d.PartyName != null && d.PartyName.Contains(s))
                                     || (d.ReferenceNo != null && d.ReferenceNo.Contains(s)) || (d.PartyPhone != null && d.PartyPhone.Contains(s)));
        }
        int total = await query.CountAsync(ct);
        int size = Math.Clamp(q.PageSize, 1, 500);
        var page = await query.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id)
            .Skip((Math.Max(q.Page, 1) - 1) * size).Take(size)
            .Select(d => new DocumentListItem(d.Id, d.VoucherType, d.DocumentNo, d.Date, d.PartyName, d.PaymentMode,
                d.TaxableAmount + d.ExemptAmount, d.CgstAmount + d.SgstAmount + d.IgstAmount + d.CessAmount + d.AddCessAmount + d.KfCessAmount,
                d.BillAmount, d.Cancelled, d.IsProcessed, db.Users.Where(u => u.Id == d.UserId).Select(u => u.UserName).FirstOrDefault(),
                d.Counter, d.ReferenceNo))
            .ToListAsync(ct);
        return new PagedResult<DocumentListItem>(page, total, q.Page, size);
    }

    public async Task LogVoidItemAsync(VoidItemDto v, CancellationToken ct)
    {
        db.VoidItems.Add(new VoidItem
        {
            UserId = user.UserId, UserName = user.UserName, Screen = v.Screen, DocumentNo = v.DocumentNo, ProductId = v.ProductId,
            ProductName = v.ProductName, Batch = v.Batch, Qty = v.Qty, Rate = v.Rate, Mrp = v.Mrp, Machine = user.Machine, At = clock.Now,
        });
        await db.SaveChangesAsync(ct);
    }
}
