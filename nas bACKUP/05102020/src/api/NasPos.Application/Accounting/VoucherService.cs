using System.Data;
using Microsoft.EntityFrameworkCore;
using NasPos.Application.Common;
using NasPos.Application.Licensing;
using NasPos.Domain;
using NasPos.Domain.Entities;
using NasPos.Domain.Posting;

namespace NasPos.Application.Accounting;

public sealed class BillAllocationDto
{
    public long BillId { get; set; }
    public decimal Amount { get; set; }
    public string? DocumentNo { get; set; }
}

public sealed class VoucherLineDto
{
    public long Id { get; set; }
    public int LineNo { get; set; }
    public long LedgerId { get; set; }
    public string? LedgerName { get; set; }
    public EntrySide Side { get; set; }
    public decimal Amount { get; set; }
    public string? Narration { get; set; }
    public List<BillAllocationDto> Allocations { get; set; } = [];
}

public sealed class VoucherDto
{
    public long Id { get; set; }
    public VoucherType VoucherType { get; set; }
    public string? DocumentNo { get; set; }
    public DateTimeOffset Date { get; set; }
    public int Version { get; set; }
    public string? RowVersion { get; set; }
    public long? HeaderLedgerId { get; set; }
    public string? HeaderLedgerName { get; set; }
    public EntrySide HeaderSide { get; set; }
    public decimal Total { get; set; }
    public string? Narration { get; set; }
    public string? ReferenceNo { get; set; }
    public string? ChequeNo { get; set; }
    public DateOnly? ChequeDate { get; set; }
    public string? Bank { get; set; }
    public ChequeStatus ChequeStatus { get; set; }
    public bool IsReconciled { get; set; }
    public DateOnly? ReconciledOn { get; set; }
    public long? SalespersonId { get; set; }
    public bool Cancelled { get; set; }
    public string? UserName { get; set; }
    public List<VoucherLineDto> Lines { get; set; } = [];
}

public sealed record VoucherListItem(long Id, VoucherType VoucherType, string DocumentNo, DateTimeOffset Date, string? HeaderLedger,
    string Ledgers, decimal Total, string? ChequeNo, ChequeStatus ChequeStatus, bool Cancelled, string? Narration);

public sealed record OpenBillDto(long Id, VoucherType VoucherType, string DocumentNo, DateTimeOffset Date, DateOnly? DueDate,
    EntrySide Side, decimal Amount, decimal Settled, decimal Balance, int AgeDays);

public enum OldBalanceMode { Cash, Card }

/// <summary>Extra money paid with a bill, to be applied to the customer's earlier outstanding.</summary>
public sealed record OldBalanceRequest(decimal Amount, OldBalanceMode Mode = OldBalanceMode.Cash);

public sealed record ReconcileRequest(long VoucherId, ChequeStatus Status, bool IsReconciled, DateOnly? ReconciledOn);

/// <summary>Receipts, payments, journals, contra, debit/credit notes and opening balances; posted transactionally.</summary>
public class VoucherService(IAppDbContext db, ICurrentUser user, IClock clock, NumberingService numbering, FinancialYearService years,
    SystemLedgerResolver systemLedgers, AuditService audit, IRealtimeNotifier notifier, LicenseService license)
{
    private static readonly VoucherType[] Allowed =
        [VoucherType.Receipt, VoucherType.Payment, VoucherType.Journal, VoucherType.Contra, VoucherType.DebitNote, VoucherType.CreditNote, VoucherType.Opening];

    public async Task<VoucherDto> SaveAsync(VoucherDto dto, long? id, CancellationToken ct)
    {
        if (!Allowed.Contains(dto.VoucherType)) throw new DomainException("Unsupported voucher type.");
        await license.EnsureCanTransactAsync(ct);
        await using var tx = await db.BeginAsync(IsolationLevel.ReadCommitted, ct);

        Voucher v;
        if (id is null)
        {
            v = new Voucher { VoucherType = dto.VoucherType, UserId = user.UserId, Counter = user.Machine };
            db.Vouchers.Add(v);
        }
        else
        {
            v = await db.Vouchers.Include(x => x.Lines).ThenInclude(l => l.Settlements).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Voucher not found.");
            if (v.Cancelled) throw new DomainException("A cancelled voucher cannot be edited.");
            if (v.VoucherType != dto.VoucherType) throw new DomainException("Voucher type cannot change on edit.");
            if (!string.IsNullOrEmpty(dto.RowVersion) && !Convert.FromBase64String(dto.RowVersion).SequenceEqual(v.RowVersion))
                throw new ConflictException("This voucher was changed by another user. Reload and try again.");
            if ((await db.FinancialYears.FirstAsync(f => f.Id == v.FinancialYearId, ct)).IsClosed)
                throw new DomainException("The voucher belongs to a closed financial year.", "year_closed");
            await ReverseAsync(v, ct);
            db.VoucherLines.RemoveRange(v.Lines);
            v.Lines.Clear();
            v.Version++;
        }

        var fy = dto.VoucherType == VoucherType.Opening
            ? await years.CurrentAsync(ct)
            : await years.ForPostingAsync(dto.Date, ct);
        if (fy.IsClosed) throw new DomainException("The financial year is closed.", "year_closed");
        if (id is not null && v.FinancialYearId != fy.Id) throw new DomainException("An edit cannot move a voucher into another financial year.");
        v.FinancialYearId = fy.Id;
        v.Date = dto.VoucherType == VoucherType.Opening ? clock.DayStart(fy.StartDate) : dto.Date;
        v.HeaderLedgerId = dto.VoucherType is VoucherType.Journal or VoucherType.Opening ? null : dto.HeaderLedgerId;
        v.HeaderSide = dto.VoucherType switch
        {
            VoucherType.Receipt => EntrySide.Dr,
            VoucherType.Payment => EntrySide.Cr,
            _ => dto.HeaderSide == 0 ? EntrySide.Dr : dto.HeaderSide,
        };
        v.Narration = dto.Narration?.Trim();
        v.ReferenceNo = dto.ReferenceNo?.Trim();
        v.ChequeNo = dto.ChequeNo?.Trim();
        v.ChequeDate = dto.ChequeDate;
        v.Bank = dto.Bank?.Trim();
        v.ChequeStatus = string.IsNullOrWhiteSpace(dto.ChequeNo) ? ChequeStatus.None : dto.ChequeStatus == ChequeStatus.None ? ChequeStatus.Pending : dto.ChequeStatus;
        v.SalespersonId = dto.SalespersonId;

        if (v.HeaderLedgerId is null && dto.VoucherType is not (VoucherType.Journal or VoucherType.Opening))
            throw new DomainException("Select the cash / bank (header) ledger.", "header_required");

        var ledgerIds = dto.Lines.Select(l => l.LedgerId).Append(v.HeaderLedgerId ?? 0).Where(i => i > 0).Distinct().ToList();
        var ledgerMap = await db.Ledgers.AsNoTracking().Include(l => l.AccountGroup).Where(l => ledgerIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        if (ledgerMap.Count != ledgerIds.Count) throw new DomainException("A ledger on this voucher was not found.");
        if (ledgerMap.Values.FirstOrDefault(l => !l.IsActive) is { } inactive) throw new DomainException($"Ledger {inactive.Name} is inactive.");
        if (dto.VoucherType == VoucherType.Contra)
        {
            bool IsCashBank(Ledger l) => l.AccountGroup!.SystemCode is SystemGroup.CashInHand or SystemGroup.BankAccounts;
            if (!ledgerMap.Values.All(IsCashBank)) throw new DomainException("Contra vouchers move money between cash and bank ledgers only.", "contra_ledgers");
        }

        EntrySide lineSide = v.HeaderSide == EntrySide.Dr ? EntrySide.Cr : EntrySide.Dr;
        int n = 1;
        foreach (var li in dto.Lines.Where(l => l.LedgerId > 0 && l.Amount != 0))
        {
            var line = new VoucherLine
            {
                LineNo = n++, LedgerId = li.LedgerId, Amount = decimal.Round(li.Amount, 2, MidpointRounding.AwayFromZero),
                Narration = li.Narration?.Trim(),
                Side = dto.VoucherType is VoucherType.Journal or VoucherType.Opening ? (li.Side == 0 ? EntrySide.Dr : li.Side) : lineSide,
            };
            v.Lines.Add(line);
        }
        v.Total = dto.VoucherType is VoucherType.Journal or VoucherType.Opening
            ? v.Lines.Where(l => l.Side == EntrySide.Dr).Sum(l => l.Amount)
            : v.Lines.Sum(l => l.Amount);

        var result = PostingEngine.PostVoucher(v, new PostingContext { Ledger = await systemLedgers.GetAsync(ct) });

        if (id is null)
        {
            var (seq, no) = await numbering.NextAsync(v.VoucherType, fy.Id, ct);
            v.SequenceNo = seq;
            v.DocumentNo = no;
        }
        await db.SaveChangesAsync(ct);

        foreach (var leg in result.Legs)
            db.AccountEntries.Add(new AccountEntry
            {
                FinancialYearId = v.FinancialYearId, VoucherType = v.VoucherType, VoucherId = v.Id, DocumentNo = v.DocumentNo,
                Date = v.Date, LedgerId = leg.LedgerId, ContraLedgerId = leg.ContraLedgerId, Debit = leg.Debit, Credit = leg.Credit,
                Remarks = leg.Remarks,
            });

        await ApplyAllocationsAsync(v, dto, ct);
        if (v.VoucherType == VoucherType.Opening) await CreateOpeningBillsAsync(v, ledgerMap, ct);

        audit.Log(Screens.ForVoucher(v.VoucherType), id is null ? "Add" : "Edit", $"{v.VoucherType.DisplayName()} {v.DocumentNo} amount {v.Total:0.00}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.DocumentSavedAsync(user.CompanyId, new { v.Id, v.DocumentNo, VoucherType = v.VoucherType.ToString(), Amount = v.Total, Action = id is null ? "Add" : "Edit" });
        return await GetAsync(v.Id, ct);
    }

    /// <summary>Bill-wise settlement: allocate each line's amount to the party's open bills.</summary>
    private async Task ApplyAllocationsAsync(Voucher v, VoucherDto dto, CancellationToken ct)
    {
        var inputLines = dto.Lines.Where(l => l.LedgerId > 0 && l.Amount != 0).ToList();
        for (int i = 0; i < inputLines.Count; i++)
        {
            var li = inputLines[i];
            var line = v.Lines[i];
            var allocations = li.Allocations.Where(a => a.Amount > 0).ToList();
            if (allocations.Count == 0) continue;
            if (allocations.Sum(a => a.Amount) > line.Amount) throw new DomainException($"Line {line.LineNo}: allocations exceed the line amount.", "allocation");
            var billIds = allocations.Select(a => a.BillId).ToList();
            var bills = await db.BillOutstandings.Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
            foreach (var a in allocations)
            {
                if (!bills.TryGetValue(a.BillId, out var bill) || bill.LedgerId != line.LedgerId || bill.Cancelled)
                    throw new DomainException("Allocated bill not found for this party.", "allocation");
                // A receipt (line Cr) settles receivables (Dr bills); a payment (line Dr) settles payables (Cr bills).
                if ((line.Side == EntrySide.Cr) != (bill.Side == EntrySide.Dr)) throw new DomainException($"Bill {bill.DocumentNo} is on the wrong side for this entry.", "allocation");
                if (a.Amount > bill.Amount - bill.Settled) throw new DomainException($"Bill {bill.DocumentNo}: allocation exceeds the balance.", "allocation");
                bill.Settled += a.Amount;
                db.BillSettlements.Add(new BillSettlement { BillId = bill.Id, VoucherLineId = line.Id, Amount = a.Amount, Date = v.Date });
            }
        }
    }

    /// <summary>Opening balances of parties become open bills so they can be settled bill-by-bill.</summary>
    private async Task CreateOpeningBillsAsync(Voucher v, Dictionary<long, Ledger> ledgers, CancellationToken ct)
    {
        foreach (var l in v.Lines.Where(l => ledgers[l.LedgerId].PartyType is PartyType.Customer or PartyType.Supplier))
            db.BillOutstandings.Add(new BillOutstanding
            {
                LedgerId = l.LedgerId, VoucherType = VoucherType.Opening, VoucherId = v.Id, DocumentNo = $"{v.DocumentNo}/{l.LineNo}",
                Date = v.Date, Side = l.Side, Amount = l.Amount,
            });
        await Task.CompletedTask;
    }

    private async Task ReverseAsync(Voucher v, CancellationToken ct)
    {
        foreach (var line in v.Lines)
            foreach (var s in line.Settlements.ToList())
            {
                var bill = await db.BillOutstandings.FirstAsync(b => b.Id == s.BillId, ct);
                bill.Settled -= s.Amount;
                db.BillSettlements.Remove(s);
            }
        var openingBills = await db.BillOutstandings.Include(b => b.Settlements).Where(b => b.VoucherId == v.Id).ToListAsync(ct);
        if (openingBills.Any(b => b.Settlements.Count > 0)) throw new DomainException("Opening bills of this voucher have settlements; remove them first.", "bill_settled");
        db.BillOutstandings.RemoveRange(openingBills);
        await db.SaveChangesAsync(ct);
        await db.AccountEntries.Where(e => e.VoucherId == v.Id).ExecuteDeleteAsync(ct);
    }

    public async Task CancelAsync(long id, CancellationToken ct)
    {
        await using var tx = await db.BeginAsync(IsolationLevel.ReadCommitted, ct);
        var v = await db.Vouchers.Include(x => x.Lines).ThenInclude(l => l.Settlements).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Voucher not found.");
        if (v.Cancelled) throw new DomainException("Already cancelled.");
        if ((await db.FinancialYears.FirstAsync(f => f.Id == v.FinancialYearId, ct)).IsClosed)
            throw new DomainException("The voucher belongs to a closed financial year.", "year_closed");
        await ReverseAsync(v, ct);
        v.Cancelled = true;
        v.CancelledAt = clock.Now;
        v.CancelledBy = user.UserId;
        audit.Log(Screens.ForVoucher(v.VoucherType), "Cancel", $"{v.VoucherType.DisplayName()} {v.DocumentNo}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<VoucherDto> GetAsync(long id, CancellationToken ct)
    {
        var v = await db.Vouchers.AsNoTracking().Include(x => x.HeaderLedger).Include(x => x.Lines).ThenInclude(l => l.Ledger)
                    .Include(x => x.Lines).ThenInclude(l => l.Settlements).ThenInclude(s => s.Bill)
                    .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Voucher not found.");
        string? userName = v.UserId is null ? null : await db.Users.Where(u => u.Id == v.UserId).Select(u => u.UserName).FirstOrDefaultAsync(ct);
        return new VoucherDto
        {
            Id = v.Id, VoucherType = v.VoucherType, DocumentNo = v.DocumentNo, Date = v.Date, Version = v.Version,
            RowVersion = Convert.ToBase64String(v.RowVersion), HeaderLedgerId = v.HeaderLedgerId, HeaderLedgerName = v.HeaderLedger?.Name,
            HeaderSide = v.HeaderSide, Total = v.Total, Narration = v.Narration, ReferenceNo = v.ReferenceNo, ChequeNo = v.ChequeNo,
            ChequeDate = v.ChequeDate, Bank = v.Bank, ChequeStatus = v.ChequeStatus, IsReconciled = v.IsReconciled,
            ReconciledOn = v.ReconciledOn, SalespersonId = v.SalespersonId, Cancelled = v.Cancelled, UserName = userName,
            Lines = v.Lines.OrderBy(l => l.LineNo).Select(l => new VoucherLineDto
            {
                Id = l.Id, LineNo = l.LineNo, LedgerId = l.LedgerId, LedgerName = l.Ledger?.Name, Side = l.Side, Amount = l.Amount,
                Narration = l.Narration,
                Allocations = l.Settlements.Select(s => new BillAllocationDto { BillId = s.BillId, Amount = s.Amount, DocumentNo = s.Bill?.DocumentNo }).ToList(),
            }).ToList(),
        };
    }

    public async Task<PagedResult<VoucherListItem>> ListAsync(VoucherType? type, DateOnly? from, DateOnly? to, long? ledgerId, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        var q = db.Vouchers.AsNoTracking().AsQueryable();
        if (type is not null) q = q.Where(v => v.VoucherType == type);
        if (from is not null) { var f = clock.DayStart(from.Value); q = q.Where(v => v.Date >= f); }
        if (to is not null) { var t = clock.DayEnd(to.Value); q = q.Where(v => v.Date < t); }
        if (ledgerId is not null) q = q.Where(v => v.HeaderLedgerId == ledgerId || v.Lines.Any(l => l.LedgerId == ledgerId));
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(v => v.DocumentNo.Contains(search) || (v.Narration != null && v.Narration.Contains(search)) || (v.ChequeNo != null && v.ChequeNo.Contains(search)));
        int total = await q.CountAsync(ct);
        int size = Math.Clamp(pageSize, 1, 500);
        var rows = await q.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).Skip((Math.Max(page, 1) - 1) * size).Take(size)
            .Select(v => new
            {
                v.Id, v.VoucherType, v.DocumentNo, v.Date, Header = v.HeaderLedger != null ? v.HeaderLedger.Name : null,
                Ledgers = v.Lines.OrderBy(l => l.LineNo).Select(l => l.Ledger!.Name).ToList(), v.Total, v.ChequeNo, v.ChequeStatus, v.Cancelled, v.Narration,
            }).ToListAsync(ct);
        return new PagedResult<VoucherListItem>(rows.Select(r => new VoucherListItem(r.Id, r.VoucherType, r.DocumentNo, r.Date, r.Header,
            string.Join(", ", r.Ledgers), r.Total, r.ChequeNo, r.ChequeStatus, r.Cancelled, r.Narration)).ToList(), total, page, size);
    }

    /// <summary>
    /// Money a customer pays at the counter beyond a bill, applied to what they already owe: e.g. owes ₹10,000, buys ₹3,000,
    /// pays ₹5,000 → the bill is paid and ₹2,000 becomes a receipt that settles their oldest open bills (balance ₹8,000).
    /// </summary>
    public async Task<VoucherDto> ReceiveOnOldBalanceAsync(long documentId, OldBalanceRequest r, CancellationToken ct)
    {
        var amount = decimal.Round(r.Amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0) throw new DomainException("Enter the amount to apply to the old balance.");
        var doc = await db.TradeDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw new NotFoundException("Bill not found.");
        if (!doc.VoucherType.IsSalesFamily()) throw new DomainException("Old balance can only be received with a sales bill.");
        if (doc.Cancelled) throw new DomainException("This bill is cancelled.");
        if (doc.PartyLedgerId is null) throw new DomainException("Select the customer on the bill first.", "party_required");
        var party = await db.Ledgers.AsNoTracking().FirstAsync(l => l.Id == doc.PartyLedgerId, ct);
        if (party.SystemCode == SystemLedger.WalkInCustomer || party.PartyType != PartyType.Customer)
            throw new DomainException("Old balance can only be received from a named customer, not the walk-in customer.", "party_required");

        var owed = await db.AccountEntries.Where(e => e.LedgerId == party.Id && e.FinancialYearId == doc.FinancialYearId)
            .SumAsync(e => (decimal?)(e.Debit - e.Credit), ct) ?? 0;
        if (owed <= 0) throw new DomainException($"{party.Name} has no outstanding balance.", "nothing_owed");
        if (amount > owed) throw new DomainException($"{party.Name} owes only ₹ {owed:0.00}; you can apply at most that.", "exceeds_balance");

        var system = await systemLedgers.GetAsync(ct);
        long header = r.Mode == OldBalanceMode.Card ? doc.CardLedgerId ?? system(SystemLedger.Bank) : system(SystemLedger.Cash);

        // Settle the oldest open bills first (opening balance, then earlier credit bills).
        var allocations = new List<BillAllocationDto>();
        var left = amount;
        foreach (var bill in await OpenBillsAsync(party.Id, ct))
        {
            if (left <= 0) break;
            if (bill.Side != EntrySide.Dr || bill.Balance <= 0) continue;
            var take = Math.Min(left, bill.Balance);
            allocations.Add(new BillAllocationDto { BillId = bill.Id, Amount = take });
            left -= take;
        }

        var receipt = await SaveAsync(new VoucherDto
        {
            VoucherType = VoucherType.Receipt, Date = clock.Now, HeaderLedgerId = header, SalespersonId = doc.SalespersonId,
            ReferenceNo = doc.DocumentNo, Narration = $"Paid towards old balance with bill {doc.DocumentNo}",
            Lines = [new VoucherLineDto { LedgerId = party.Id, Amount = amount, Narration = $"Old balance — with bill {doc.DocumentNo}", Allocations = allocations }],
        }, null, ct);
        return receipt;
    }

    public async Task<List<OpenBillDto>> OpenBillsAsync(long ledgerId, CancellationToken ct)
    {
        var today = clock.Today();
        var bills = await db.BillOutstandings.AsNoTracking().Where(b => b.LedgerId == ledgerId && !b.Cancelled && b.Settled < b.Amount)
            .OrderBy(b => b.Date).ToListAsync(ct);
        return bills.Select(b => new OpenBillDto(b.Id, b.VoucherType, b.DocumentNo, b.Date, b.DueDate, b.Side, b.Amount, b.Settled,
            b.Amount - b.Settled, today.DayNumber - clock.BusinessDate(b.Date).DayNumber)).ToList();
    }

    public async Task ReconcileAsync(ReconcileRequest r, CancellationToken ct)
    {
        var v = await db.Vouchers.FirstOrDefaultAsync(x => x.Id == r.VoucherId, ct) ?? throw new NotFoundException("Voucher not found.");
        v.ChequeStatus = r.Status;
        v.IsReconciled = r.IsReconciled;
        v.ReconciledOn = r.IsReconciled ? r.ReconciledOn ?? clock.Today() : null;
        audit.Log(Screens.Voucher, "Edit", $"Cheque {v.ChequeNo} on {v.DocumentNo} -> {r.Status}");
        await db.SaveChangesAsync(ct);
    }
}
