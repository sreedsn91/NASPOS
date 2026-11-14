import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { today } from '../../core/format';
import { Employee, IdName, LookupItem, PartyLookup, ProductLookup, ReportDefinition, ReportResult } from '../../core/models';
import { Ui } from '../../core/ui';
import { LookupComponent, ReportGridComponent } from '../../shared/components';

let catalogCache: ReportDefinition[] | null = null;
async function catalog(api: Api): Promise<ReportDefinition[]> {
  return catalogCache ??= await api.get<ReportDefinition[]>('/reports');
}

@Component({
  selector: 'app-report-catalog',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page">
      <div class="page-header"><h1>Reports</h1><input style="width:280px" placeholder="Find a report…" [(ngModel)]="q" /></div>
      <div class="cols">
        @for (g of groups(); track g.name) {
          <div class="card">
            <header><h2>{{ g.name }}</h2><span class="badge">{{ g.items.length }}</span></header>
            <section class="stack" style="gap:6px">
              @for (r of g.items; track r.key) {
                <a [routerLink]="['/reports', r.key]">{{ r.title }}</a>
                @if (r.description) { <span class="muted small" style="margin-top:-6px">{{ r.description }}</span> }
              }
            </section>
          </div>
        } @empty { <div class="empty">No reports available for your role.</div> }
      </div>
    </div>`,
})
export class ReportCatalogComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  defs = signal<ReportDefinition[]>([]);
  q = '';
  groups = computed(() => {
    const q = this.q.toLowerCase();
    const map = new Map<string, ReportDefinition[]>();
    for (const d of this.defs().filter(d => !q || d.title.toLowerCase().includes(q) || d.group.toLowerCase().includes(q))) {
      if (!map.has(d.group)) map.set(d.group, []);
      map.get(d.group)!.push(d);
    }
    return [...map.entries()].map(([name, items]) => ({ name, items }));
  });
  async ngOnInit(): Promise<void> {
    try { this.defs.set(await catalog(this.api)); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

interface Filter {
  from: string; to: string; voucherType?: string | null; paymentMode?: string | null; taxType?: string | null; partyId?: number | null;
  ledgerId?: number | null; groupId?: number | null; categoryId?: number | null; productId?: number | null; manufacturerId?: number | null;
  salespersonId?: number | null; areaId?: number | null; itemClass?: string | null; counter?: string; days?: number | null; detailed: boolean; side?: string;
}

@Component({
  selector: 'app-report-viewer',
  standalone: true,
  imports: [FormsModule, RouterLink, LookupComponent, ReportGridComponent],
  template: `
    <div class="page wide">
      <div class="page-header">
        <a class="btn ghost" routerLink="/reports">‹ Reports</a>
        <h1>{{ def()?.title ?? key() }}</h1>
        <button class="btn primary" (click)="run()">Run</button>
        @if (auth.can(def()?.screen ?? '', 'Export')) { <button class="btn" (click)="exportAs('xlsx')" [disabled]="!result()">Excel</button> }
        @if (auth.can(def()?.screen ?? '', 'Print')) { <button class="btn" (click)="exportAs('pdf')" [disabled]="!result()">PDF / Print</button> }
        @if (auth.can(def()?.screen ?? '', 'Email')) { <button class="btn" (click)="email()" [disabled]="!result()">E-mail</button> }
        @if (key().startsWith('gstr1') && auth.can('TaxReports', 'Export')) { <button class="btn" (click)="gstr1()">GSTR-1 workbook</button> }
      </div>
      @if (def(); as d) {
        <div class="card"><section>
          <div class="form-grid cols-6">
            @if (has('dates')) {
              <label class="field"><span>From</span><input type="date" [(ngModel)]="f.from" /></label>
              <label class="field"><span>To</span><input type="date" [(ngModel)]="f.to" /></label>
            }
            @if (has('asOn')) { <label class="field"><span>As on</span><input type="date" [(ngModel)]="f.to" /></label> }
            @if (has('voucherType')) {
              <label class="field"><span>Voucher type</span><select [(ngModel)]="f.voucherType"><option [ngValue]="null">All</option>
                @for (v of voucherTypes; track v) { <option [value]="v">{{ v }}</option> }</select></label>
            }
            @if (has('paymentMode')) {
              <label class="field"><span>Payment</span><select [(ngModel)]="f.paymentMode"><option [ngValue]="null">All</option>
                <option value="Cash">Cash</option><option value="Credit">Credit</option><option value="Mixed">Mixed</option></select></label>
            }
            @if (has('taxType')) {
              <label class="field"><span>Tax type</span><select [(ngModel)]="f.taxType"><option [ngValue]="null">All</option>
                <option value="IntraState">Intra-state</option><option value="InterState">Inter-state</option><option value="NoTax">No tax</option></select></label>
            }
            @if (has('party') || has('customer') || has('supplier')) {
              <div class="field span-2"><span>{{ has('supplier') ? 'Supplier' : has('customer') ? 'Customer' : 'Party' }}</span>
                <app-lookup [search]="searchParty" [display]="name" placeholder="All parties" (picked)="f.partyId = $event.id" (entered)="f.partyId = null" /></div>
            }
            @if (has('ledger')) {
              <div class="field span-2"><span>Ledger</span>
                <app-lookup [search]="searchLedger" [display]="name" [value]="ledgerName" placeholder="Choose ledger" (picked)="f.ledgerId = $event.id; ledgerName = $event.name" /></div>
            }
            @if (has('group')) {
              <label class="field"><span>Group</span><select [(ngModel)]="f.groupId"><option [ngValue]="null">All</option>
                @for (g of groups(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label>
            }
            @if (has('category')) {
              <label class="field"><span>Category</span><select [(ngModel)]="f.categoryId"><option [ngValue]="null">All</option>
                @for (c of categories(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
            }
            @if (has('manufacturer')) {
              <label class="field"><span>Manufacturer</span><select [(ngModel)]="f.manufacturerId"><option [ngValue]="null">All</option>
                @for (c of manufacturers(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
            }
            @if (has('area')) {
              <label class="field"><span>Area</span><select [(ngModel)]="f.areaId"><option [ngValue]="null">All</option>
                @for (c of areas(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
            }
            @if (has('product')) {
              <div class="field span-2"><span>Item</span>
                <app-lookup [search]="searchProduct" [display]="productName" [value]="productText" placeholder="All items" (picked)="f.productId = $event.productId; productText = $event.name" /></div>
            }
            @if (has('salesperson')) {
              <label class="field"><span>Salesperson</span><select [(ngModel)]="f.salespersonId"><option [ngValue]="null">All</option>
                @for (e of employees(); track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }</select></label>
            }
            @if (has('itemClass')) {
              <label class="field"><span>Item class</span><select [(ngModel)]="f.itemClass"><option [ngValue]="null">All</option><option value="Goods">Goods</option><option value="Service">Service</option></select></label>
            }
            @if (has('counter')) { <label class="field"><span>Counter</span><input [(ngModel)]="f.counter" placeholder="All" /></label> }
            @if (has('days')) { <label class="field"><span>Days</span><input type="number" class="num" [(ngModel)]="f.days" /></label> }
            @if (has('side')) {
              <label class="field"><span>Side</span><select [(ngModel)]="f.side"><option value="Receivable">Receivable (customers)</option><option value="Payable">Payable (suppliers)</option></select></label>
            }
            @if (has('detailed') || key().startsWith('stock-')) { <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="f.detailed" /> {{ key().startsWith('stock-') ? 'Include zero stock' : 'Detailed' }}</label> }
          </div>
        </section></div>
      }
      @if (error()) { <div class="error-box" style="margin-top:12px">{{ error() }}</div> }
      @if (result(); as r) {
        <div style="margin-top:12px">
          <div class="muted" style="margin-bottom:6px">{{ r.title }} @if (r.subtitle) { · {{ r.subtitle }} } · {{ r.rows.length }} rows</div>
          <app-report-grid [report]="r" (link)="drill($event)" />
        </div>
      }
    </div>`,
})
export class ReportViewerComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  key = signal('');
  def = signal<ReportDefinition | null>(null);
  result = signal<ReportResult | null>(null);
  error = signal('');
  groups = signal<IdName[]>([]);
  categories = signal<LookupItem[]>([]);
  manufacturers = signal<LookupItem[]>([]);
  areas = signal<LookupItem[]>([]);
  employees = signal<Employee[]>([]);
  ledgerName = '';
  productText = '';
  voucherTypes = ['Sales', 'LocalSales', 'POSSales', 'SalesReturn', 'Purchase', 'LocalPurchase', 'PurchaseReturn', 'Estimate', 'SalesOrder', 'PurchaseOrder',
    'DeliveryNote', 'ReceiptNote', 'Receipt', 'Payment', 'Journal', 'Contra', 'DebitNote', 'CreditNote', 'Opening'];
  f: Filter = this.blank();
  name = (x: { name: string }) => x.name;
  productName = (x: ProductLookup) => x.name;

  searchParty = (t: string) => {
    const type = this.has('supplier') ? 'Supplier' : 'Customer';
    return this.api.get<PartyLookup[]>('/ledgers/parties', { type, search: t, take: 15 });
  };
  searchLedger = async (t: string) => (await this.api.get<{ items: { id: number; name: string; code: string }[] }>('/ledgers', { search: t, pageSize: 20 })).items;
  searchProduct = (t: string) => this.api.get<ProductLookup[]>('/products/lookup', { term: t, take: 20 });

  private blank(): Filter {
    const fy = this.auth.session()?.financialYear;
    const t = today();
    const to = fy && t > fy.endDate ? fy.endDate : fy && t < fy.startDate ? fy.startDate : t;
    return { from: fy?.startDate ?? t, to, detailed: false, side: 'Receivable', days: 30 };
  }

  has(f: string): boolean { return this.def()?.filters.includes(f) ?? false; }

  ngOnInit(): void {
    this.route.paramMap.subscribe(async pm => {
      const key = pm.get('key')!;
      this.key.set(key);
      this.result.set(null);
      this.error.set('');
      const qp = this.route.snapshot.queryParamMap;
      this.f = this.blank();
      if (qp.get('ledgerId')) { this.f.ledgerId = +qp.get('ledgerId')!; this.ledgerName = qp.get('ledgerName') ?? ''; }
      if (qp.get('productId')) this.f.productId = +qp.get('productId')!;
      if (qp.get('from')) this.f.from = qp.get('from')!;
      if (qp.get('to')) this.f.to = qp.get('to')!;
      try {
        const defs = await catalog(this.api);
        const d = defs.find(x => x.key === key) ?? null;
        this.def.set(d);
        if (!d) { this.error.set('This report is not available for your role.'); return; }
        await this.loadRefs(d);
        const needs = (d.filters.includes('ledger') && key !== 'bank-book' && !this.f.ledgerId) || (key === 'stock-history' && !this.f.productId);
        if (!needs) await this.run();
      } catch (e) { this.error.set(errorMessage(e)); }
    });
  }

  private async loadRefs(d: ReportDefinition): Promise<void> {
    const tasks: Promise<unknown>[] = [];
    if (d.filters.includes('group') && !this.groups().length) tasks.push(this.api.get<IdName[]>('/account-groups').then(x => this.groups.set(x)));
    if (d.filters.includes('category') && !this.categories().length) tasks.push(this.api.get<LookupItem[]>('/lookups/category').then(x => this.categories.set(x)));
    if (d.filters.includes('manufacturer') && !this.manufacturers().length) tasks.push(this.api.get<LookupItem[]>('/lookups/manufacturer').then(x => this.manufacturers.set(x)));
    if (d.filters.includes('area') && !this.areas().length) tasks.push(this.api.get<LookupItem[]>('/lookups/area').then(x => this.areas.set(x)));
    if (d.filters.includes('salesperson') && !this.employees().length) tasks.push(this.api.get<Employee[]>('/employees').then(x => this.employees.set(x)));
    await Promise.all(tasks);
  }

  private body(): Record<string, unknown> {
    const b: Record<string, unknown> = { ...this.f };
    if (this.has('asOn') && !this.has('dates')) b['from'] = null;
    if (!b['counter']) b['counter'] = null;
    return b;
  }

  async run(): Promise<void> {
    this.error.set('');
    try { this.result.set(await this.ui.run(() => this.api.post<ReportResult>(`/reports/${this.key()}`, this.body()))); }
    catch (e) { this.error.set(errorMessage(e)); }
  }

  async exportAs(fmt: 'xlsx' | 'pdf'): Promise<void> {
    try {
      if (fmt === 'pdf') await this.api.openPdf(`/reports/${this.key()}/export`, this.body(), { format: 'pdf' });
      else await this.api.download(`/reports/${this.key()}/export`, this.body(), { format: 'xlsx' }, `${this.key()}.xlsx`);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async email(): Promise<void> {
    const to = await this.ui.prompt('E-mail report', 'Recipient e-mail');
    if (!to) return;
    try { await this.api.post(`/reports/${this.key()}/email`, this.body(), { to, format: 'pdf' }); this.ui.ok('Report e-mail queued.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  async gstr1(): Promise<void> {
    try { await this.api.download('/reports/gstr1/workbook', this.body(), undefined, 'GSTR1.xlsx'); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  drill(link: string): void {
    const [kind, id] = link.split(':');
    if (kind === 'document') this.router.navigate(['/doc', 'Sales', id]);
    else if (kind === 'voucher') this.router.navigate(['/voucher', 'Receipt', id]);
    else if (kind === 'ledger') this.router.navigate(['/reports', 'ledger-statement'], { queryParams: { ledgerId: id, from: this.f.from, to: this.f.to } });
  }
}
