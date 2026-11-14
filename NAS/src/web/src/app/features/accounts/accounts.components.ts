import { Component, HostListener, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { r2 } from '../../core/calc';
import { DmyPipe, MoneyPipe, fromLocalInput, monthStart, toLocalInput, today } from '../../core/format';
import { EntrySide, OpenBill, Paged, ReportResult, VOUCHER_LABELS, Voucher, VoucherLine, VoucherType } from '../../core/models';
import { Ui } from '../../core/ui';
import { LookupComponent, ModalComponent, PagerComponent, ReportGridComponent } from '../../shared/components';

interface LedgerRow { id: number; code: string; name: string; groupName?: string; partyType: string; balance: number; }

const searchLedgers = (api: Api) => async (t: string) =>
  (await api.get<Paged<LedgerRow>>('/ledgers', { search: t, active: true, pageSize: 20 })).items;

@Component({
  selector: 'app-voucher-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, LookupComponent, ModalComponent, MoneyPipe, DmyPipe],
  template: `
  <div class="page">
    <div class="page-header">
      <h1>{{ labels[type()] }} @if (v().documentNo) { <span class="muted">· {{ v().documentNo }}</span> }</h1>
      @if (v().cancelled) { <span class="badge danger">CANCELLED</span> }
      <a class="btn ghost" routerLink="/vouchers">List</a>
      @if (v().id) {
        <button class="btn" (click)="print()">Print</button>
        @if (!v().cancelled && auth.can('Voucher', 'Cancel')) { <button class="btn danger" (click)="cancel()">Cancel</button> }
        <button class="btn" (click)="router.navigate(['/voucher', type(), 'new'])">New</button>
      }
      <button class="btn primary" (click)="save()" [disabled]="readOnly() || saving()">Save <kbd>F2</kbd></button>
    </div>
    <div class="tabs">
      @for (t of types; track t) { <button [class.active]="t === type()" (click)="switchType(t)" [disabled]="!!v().id">{{ labels[t] }}</button> }
    </div>
    <div class="card"><section>
      <div class="form-grid cols-4">
        <label class="field"><span>Date</span><input type="datetime-local" [ngModel]="dateInput()" (ngModelChange)="setDate($event)" [disabled]="readOnly()" /></label>
        @if (hasHeader()) {
          <div class="field span-2"><span>{{ type() === 'Receipt' ? 'Received into (cash / bank)' : type() === 'Payment' ? 'Paid from (cash / bank)' : 'Account' }}</span>
            <app-lookup [search]="searchLedger" [display]="ledgerName" [sub]="ledgerSub" [value]="v().headerLedgerName" (picked)="v().headerLedgerId = $event.id; v().headerLedgerName = $event.name" [disabled]="readOnly()" /></div>
          <label class="field"><span>Header side</span>
            <select [(ngModel)]="v().headerSide" [disabled]="readOnly() || type() === 'Receipt' || type() === 'Payment'"><option value="Dr">Debit</option><option value="Cr">Credit</option></select></label>
        }
        <label class="field span-2"><span>Narration</span><input [(ngModel)]="v().narration" [disabled]="readOnly()" /></label>
        <label class="field"><span>Reference</span><input [(ngModel)]="v().referenceNo" [disabled]="readOnly()" /></label>
        @if (type() !== 'Journal' && type() !== 'Opening') {
          <label class="field"><span>Cheque no</span><input [(ngModel)]="v().chequeNo" [disabled]="readOnly()" /></label>
          <label class="field"><span>Cheque date</span><input type="date" [(ngModel)]="v().chequeDate" [disabled]="readOnly()" /></label>
          <label class="field"><span>Bank</span><input [(ngModel)]="v().bank" [disabled]="readOnly()" /></label>
        }
      </div>
    </section></div>

    <div class="card" style="margin-top:12px">
      <table class="line-grid">
        <thead><tr><th>#</th><th style="min-width:260px">Ledger</th>@if (perLineSide()) {<th class="w-s">Dr/Cr</th>}<th class="w-l num">Amount</th><th>Narration</th><th>Bills</th><th></th></tr></thead>
        <tbody>
          @for (l of v().lines; track $index; let i = $index) {
            <tr>
              <td class="ro">{{ i + 1 }}</td>
              <td><app-lookup [search]="searchLedger" [display]="ledgerName" [sub]="ledgerSub" [value]="l.ledgerName" (picked)="pickLine(l, $event)" [disabled]="readOnly()" /></td>
              @if (perLineSide()) { <td><select [(ngModel)]="l.side" (ngModelChange)="touch()" [disabled]="readOnly()"><option value="Dr">Dr</option><option value="Cr">Cr</option></select></td> }
              <td><input type="number" class="num" [(ngModel)]="l.amount" (ngModelChange)="touch()" [disabled]="readOnly()" /></td>
              <td><input [(ngModel)]="l.narration" [disabled]="readOnly()" /></td>
              <td class="ro">@if (l.ledgerId && canAllocate()) {
                <button class="btn sm" (click)="openBills(l)" [disabled]="readOnly()">{{ l.allocations.length ? l.allocations.length + ' bill(s)' : 'Allocate' }}</button> }</td>
              <td class="actions"><button class="btn ghost sm" (click)="v().lines.splice(i, 1); touch()" [disabled]="readOnly()">✕</button></td>
            </tr>
          }
        </tbody>
      </table>
      <div class="toolbar" style="padding:8px">
        <button class="btn sm" (click)="addLine()" [disabled]="readOnly()">+ Add line</button>
        <span class="grow"></span>
        @if (perLineSide()) { <span>Dr {{ dr() | money }} · Cr {{ cr() | money }}</span>
          @if (dr() !== cr()) { <span class="badge danger">Difference {{ dr() - cr() | money }}</span> } @else { <span class="badge ok">Balanced</span> } }
        @else { <b>Total ₹ {{ total() | money }}</b> }
      </div>
    </div>
  </div>

  @if (billLine(); as l) {
    <app-modal title="Bill-wise settlement" size="lg" (closed)="billLine.set(null)">
      <div class="toolbar" style="margin-bottom:8px"><span>Line amount ₹ {{ l.amount | money }} · allocated ₹ {{ allocated() | money }}</span><span class="grow"></span>
        <button class="btn sm" (click)="autoAllocate()">Auto (oldest first)</button><button class="btn sm" (click)="clearAlloc()">Clear</button></div>
      <table class="grid">
        <thead><tr><th>Bill</th><th>Date</th><th>Due</th><th class="num">Amount</th><th class="num">Balance</th><th class="num">Age</th><th class="num">Allocate</th></tr></thead>
        <tbody>
          @for (b of bills(); track b.id) {
            <tr><td>{{ b.documentNo }} <span class="badge">{{ b.side }}</span></td><td>{{ b.date | dmy }}</td><td>{{ b.dueDate | dmy }}</td>
              <td class="num">{{ b.amount | money }}</td><td class="num">{{ b.balance | money }}</td><td class="num">{{ b.ageDays }}</td>
              <td style="width:120px"><input type="number" class="num" [(ngModel)]="alloc[b.id]" /></td></tr>
          } @empty { <tr><td colspan="7" class="empty">No open bills for this party.</td></tr> }
        </tbody>
      </table>
      <button footer class="btn primary" (click)="applyAlloc()">Apply</button>
    </app-modal>
  }`,
})
export class VoucherEditorComponent implements OnInit {
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  labels = VOUCHER_LABELS;
  types: VoucherType[] = ['Receipt', 'Payment', 'Journal', 'Contra', 'DebitNote', 'CreditNote'];
  type = signal<VoucherType>('Receipt');
  v = signal<Voucher>(this.blank('Receipt'));
  tick = signal(0);
  saving = signal(false);
  billLine = signal<VoucherLine | null>(null);
  bills = signal<OpenBill[]>([]);
  alloc: Record<number, number> = {};
  searchLedger = searchLedgers(this.api);
  ledgerName = (l: LedgerRow) => l.name;
  ledgerSub = (l: LedgerRow) => `${l.groupName ?? ''} · ${l.balance.toFixed(2)}`;

  hasHeader = computed(() => !['Journal', 'Opening'].includes(this.type()));
  perLineSide = computed(() => !this.hasHeader());
  canAllocate = computed(() => this.auth.flag('EnableBillByBill') && this.type() !== 'Contra');
  readOnly = computed(() => !!this.v().cancelled || !!this.auth.session()?.financialYear.isClosed
    || !this.auth.can('Voucher', this.v().id ? 'Edit' : 'Add'));
  dateInput = computed(() => { this.tick(); return toLocalInput(this.v().date); });
  total = computed(() => { this.tick(); return r2(this.v().lines.reduce((s, l) => s + (+l.amount || 0), 0)); });
  dr = computed(() => { this.tick(); return r2(this.v().lines.filter(l => l.side === 'Dr').reduce((s, l) => s + (+l.amount || 0), 0)); });
  cr = computed(() => { this.tick(); return r2(this.v().lines.filter(l => l.side === 'Cr').reduce((s, l) => s + (+l.amount || 0), 0)); });
  allocated = computed(() => { this.tick(); return r2(Object.values(this.alloc).reduce((s, x) => s + (+x || 0), 0)); });

  private blank(t: VoucherType): Voucher {
    return { id: 0, voucherType: t, date: new Date().toISOString(), headerSide: t === 'Payment' ? 'Cr' : 'Dr', total: 0,
      lines: [{ ledgerId: 0, side: 'Dr', amount: 0, allocations: [] }, ...(t === 'Journal' ? [{ ledgerId: 0, side: 'Cr' as EntrySide, amount: 0, allocations: [] }] : [])] };
  }

  ngOnInit(): void {
    this.route.paramMap.subscribe(async pm => {
      const t = pm.get('type') as VoucherType;
      const id = pm.get('id');
      try {
        if (id && id !== 'new') {
          const v = await this.api.get<Voucher>(`/vouchers/${id}`);
          this.type.set(v.voucherType);
          this.v.set(v);
        } else {
          await this.loadNew(t);
        }
        this.touch();
      } catch (e) { this.ui.error(errorMessage(e)); }
    });
  }

  private async loadNew(t: VoucherType): Promise<void> {
    this.type.set(t);
    const b = this.blank(t);
    if (['Receipt', 'Payment'].includes(t)) {
      const cash = (await this.api.get<Paged<LedgerRow>>('/ledgers', { search: 'CASH', pageSize: 5 })).items.find(l => l.code === 'CASH');
      if (cash) { b.headerLedgerId = cash.id; b.headerLedgerName = cash.name; }
    }
    this.v.set(b);
    this.touch();
  }

  touch(): void { this.tick.update(x => x + 1); }
  switchType(t: VoucherType): void { this.router.navigate(['/voucher', t, 'new']); }
  setDate(val: string): void { if (val) { this.v().date = fromLocalInput(val); this.touch(); } }
  addLine(): void {
    const side: EntrySide = this.perLineSide() ? (this.dr() > this.cr() ? 'Cr' : 'Dr') : 'Dr';
    this.v().lines.push({ ledgerId: 0, side, amount: this.perLineSide() ? Math.abs(this.dr() - this.cr()) : 0, allocations: [] });
    this.touch();
  }
  pickLine(l: VoucherLine, row: LedgerRow): void { l.ledgerId = row.id; l.ledgerName = row.name; l.allocations = []; }

  async openBills(l: VoucherLine): Promise<void> {
    try {
      const all = await this.api.get<OpenBill[]>(`/vouchers/open-bills/${l.ledgerId}`);
      const lineSide: EntrySide = this.perLineSide() ? l.side : this.v().headerSide === 'Dr' ? 'Cr' : 'Dr';
      // Credit entries settle receivables (Dr bills) and debit entries settle payables (Cr bills).
      this.bills.set(all.filter(b => (lineSide === 'Cr') === (b.side === 'Dr')));
      this.alloc = {};
      for (const a of l.allocations) this.alloc[a.billId] = a.amount;
      this.billLine.set(l);
      this.touch();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  autoAllocate(): void {
    let left = +this.billLine()!.amount || 0;
    this.alloc = {};
    for (const b of this.bills()) { if (left <= 0) break; const a = Math.min(left, b.balance); this.alloc[b.id] = r2(a); left = r2(left - a); }
    this.touch();
  }
  clearAlloc(): void { this.alloc = {}; this.touch(); }
  applyAlloc(): void {
    const l = this.billLine()!;
    const list = Object.entries(this.alloc).filter(([, a]) => +a > 0).map(([id, a]) => ({ billId: +id, amount: +a, documentNo: this.bills().find(b => b.id === +id)?.documentNo }));
    const sum = list.reduce((s, x) => s + x.amount, 0);
    if (sum > (+l.amount || 0) + 0.001) { this.ui.error('Allocations exceed the line amount.'); return; }
    l.allocations = list;
    this.billLine.set(null);
  }

  async save(): Promise<void> {
    const v = this.v();
    v.lines = v.lines.filter(l => l.ledgerId && +l.amount);
    if (!v.lines.length) { this.ui.error('Add at least one ledger with an amount.'); this.addLine(); return; }
    if (this.perLineSide() && this.dr() !== this.cr()) { this.ui.error('Debits and credits must be equal.'); return; }
    this.saving.set(true);
    try {
      const body = { ...v, voucherType: this.type(), chequeDate: v.chequeDate || null };
      const saved = v.id ? await this.api.put<Voucher>(`/vouchers/${v.id}`, body) : await this.api.post<Voucher>('/vouchers', body);
      this.ui.ok(`${this.labels[saved.voucherType]} ${saved.documentNo} saved · ₹ ${saved.total.toFixed(2)}`);
      if (v.id) this.v.set(saved); else await this.loadNew(this.type());
      this.touch();
    } catch (e) { this.ui.error(errorMessage(e)); } finally { this.saving.set(false); }
  }

  async print(): Promise<void> { try { await this.api.openPdf(`/vouchers/${this.v().id}/pdf`); } catch (e) { this.ui.error(errorMessage(e)); } }

  async cancel(): Promise<void> {
    if (!(await this.ui.confirm('Cancel voucher', `Cancel ${this.v().documentNo}? Its postings and bill settlements are reversed.`, { danger: true, okText: 'Cancel voucher' }))) return;
    try { await this.api.post(`/vouchers/${this.v().id}/cancel`); this.v.set(await this.api.get<Voucher>(`/vouchers/${this.v().id}`)); this.ui.ok('Cancelled.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  @HostListener('window:keydown', ['$event'])
  keys(e: KeyboardEvent): void { if (e.key === 'F2') { e.preventDefault(); this.save(); } }
}

interface VoucherListItem { id: number; voucherType: VoucherType; documentNo: string; date: string; headerLedger?: string; ledgers: string; total: number; chequeNo?: string; chequeStatus: string; cancelled: boolean; narration?: string; }

@Component({
  selector: 'app-voucher-list',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe, DmyPipe, PagerComponent],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Voucher search</h1>
        @if (auth.can('Voucher', 'Add')) { <a class="btn primary" routerLink="/voucher/Receipt/new">New voucher</a> }</div>
      <div class="card"><section class="form-grid cols-6">
        <label class="field"><span>Type</span><select [(ngModel)]="type" (ngModelChange)="load(1)"><option value="">All</option>
          @for (t of types; track t) { <option [value]="t">{{ labels[t] }}</option> }</select></label>
        <label class="field"><span>From</span><input type="date" [(ngModel)]="from" (change)="load(1)" /></label>
        <label class="field"><span>To</span><input type="date" [(ngModel)]="to" (change)="load(1)" /></label>
        <label class="field span-2"><span>Search (no, narration, cheque)</span><input [(ngModel)]="search" (keydown.enter)="load(1)" /></label>
      </section></div>
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th>Date</th><th>Type</th><th>No</th><th>Header</th><th>Ledgers</th><th>Narration</th><th>Cheque</th><th class="num">Amount</th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr class="clickable" [class.cancelled]="r.cancelled" (click)="router.navigate(['/voucher', r.voucherType, r.id])">
                <td>{{ r.date | dmy }}</td><td>{{ labels[r.voucherType] }}</td><td><b>{{ r.documentNo }}</b></td><td>{{ r.headerLedger }}</td>
                <td>{{ r.ledgers }}</td><td class="muted">{{ r.narration }}</td><td>{{ r.chequeNo }} @if (r.chequeNo) {<span class="badge">{{ r.chequeStatus }}</span>}</td>
                <td class="num">{{ r.total | money }}</td></tr>
            } @empty { <tr><td colspan="8" class="empty">No vouchers.</td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="50" (go)="load($event)" />
    </div>`,
})
export class VoucherListComponent implements OnInit {
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  labels = VOUCHER_LABELS;
  types: VoucherType[] = ['Receipt', 'Payment', 'Journal', 'Contra', 'DebitNote', 'CreditNote', 'Opening'];
  type = '';
  from = monthStart();
  to = today();
  search = '';
  rows = signal<VoucherListItem[]>([]);
  total = signal(0);
  page = signal(1);
  ngOnInit(): void { this.load(1); }
  async load(p: number): Promise<void> {
    try {
      const r = await this.api.get<Paged<VoucherListItem>>('/vouchers', { type: this.type || undefined, from: this.from, to: this.to, search: this.search, page: p });
      this.rows.set(r.items); this.total.set(r.total); this.page.set(p);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

interface OpeningRow { ledgerId: number; code: string; name: string; groupName?: string; amount: number; side: EntrySide; _orig?: string; }

@Component({
  selector: 'app-opening-balances',
  standalone: true,
  imports: [FormsModule, MoneyPipe],
  template: `
    <div class="page">
      <div class="page-header"><h1>Opening balances · FY {{ auth.session()?.financialYear?.name }}</h1>
        <input style="width:240px" placeholder="Filter ledgers…" [(ngModel)]="q" />
        <button class="btn primary" (click)="saveAll()" [disabled]="!dirty().length || !auth.can('OpeningBalance', 'Edit')">Save {{ dirty().length || '' }} change(s)</button></div>
      <div class="info-box" style="margin-bottom:10px">Each balance is posted against the “Opening Balance Difference” ledger. Customer and supplier openings become open bills for bill-by-bill settlement.
        Totals: Dr {{ totDr() | money }} · Cr {{ totCr() | money }} · difference {{ totDr() - totCr() | money }}</div>
      <div class="table-wrap" style="max-height:calc(100vh - 220px)">
        <table class="grid">
          <thead><tr><th>Code</th><th>Ledger</th><th>Group</th><th class="num" style="width:160px">Amount</th><th style="width:90px">Dr/Cr</th></tr></thead>
          <tbody>
            @for (r of filtered(); track r.ledgerId) {
              <tr><td>{{ r.code }}</td><td>{{ r.name }}</td><td class="muted">{{ r.groupName }}</td>
                <td><input type="number" class="num" [(ngModel)]="r.amount" (ngModelChange)="t.set(t() + 1)" /></td>
                <td><select [(ngModel)]="r.side" (ngModelChange)="t.set(t() + 1)"><option value="Dr">Dr</option><option value="Cr">Cr</option></select></td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>`,
})
export class OpeningBalancesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<OpeningRow[]>([]);
  t = signal(0);
  q = '';
  filtered = computed(() => { this.t(); const q = this.q.toLowerCase(); return this.rows().filter(r => !q || r.name.toLowerCase().includes(q) || r.code.toLowerCase().includes(q)); });
  dirty = computed(() => { this.t(); return this.rows().filter(r => r._orig !== `${+r.amount || 0}|${r.side}`); });
  totDr = computed(() => { this.t(); return this.rows().filter(r => r.side === 'Dr').reduce((s, r) => s + (+r.amount || 0), 0); });
  totCr = computed(() => { this.t(); return this.rows().filter(r => r.side === 'Cr').reduce((s, r) => s + (+r.amount || 0), 0); });
  async ngOnInit(): Promise<void> { await this.load(); }
  private async load(): Promise<void> {
    try {
      const rows = await this.api.get<OpeningRow[]>('/ledgers/opening-balances');
      rows.forEach(r => (r._orig = `${r.amount}|${r.side}`));
      this.rows.set(rows); this.t.set(this.t() + 1);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async saveAll(): Promise<void> {
    try {
      await this.ui.run(async () => { for (const r of this.dirty()) await this.api.put(`/ledgers/${r.ledgerId}/opening`, { ...r, amount: +r.amount || 0 }); });
      this.ui.ok('Opening balances saved.');
      await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); await this.load(); }
  }
}

@Component({
  selector: 'app-cheques',
  standalone: true,
  imports: [FormsModule, ReportGridComponent],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Cheque register &amp; reconciliation</h1>
        <label class="field"><span>From</span><input type="date" [(ngModel)]="from" /></label>
        <label class="field"><span>To</span><input type="date" [(ngModel)]="to" /></label>
        <button class="btn primary" (click)="load()">Show</button></div>
      <div class="muted" style="margin-bottom:8px">Click a cheque to mark it cleared, bounced or pending.</div>
      <app-report-grid [report]="report()" (link)="reconcile($event)" />
    </div>`,
})
export class ChequesComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  from = monthStart();
  to = today();
  report = signal<ReportResult | null>(null);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> {
    try { this.report.set(await this.api.post<ReportResult>('/reports/cheque-register', { from: this.from, to: this.to })); } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async reconcile(link: string): Promise<void> {
    const id = +link.split(':')[1];
    const status = await this.ui.prompt('Cheque status', 'Type Cleared, Bounced or Pending', { value: 'Cleared' });
    if (!status) return;
    const s = ['Cleared', 'Bounced', 'Pending'].find(x => x.toLowerCase() === status.trim().toLowerCase());
    if (!s) { this.ui.error('Enter Cleared, Bounced or Pending.'); return; }
    try {
      await this.api.post('/vouchers/reconcile', { voucherId: id, status: s, isReconciled: s === 'Cleared', reconciledOn: s === 'Cleared' ? today() : null });
      this.ui.ok('Updated.');
      await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}
