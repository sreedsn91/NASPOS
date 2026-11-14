import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { monthStart, today } from '../../core/format';
import { CompanyDto, DocumentListItem, Paged, VOUCHER_LABELS } from '../../core/models';
import { Ui } from '../../core/ui';

interface ImportResult { created: number; updated: number; skipped: number; errors: string[]; openingStockDocumentId?: number | null; }

@Component({
  selector: 'app-import-export',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:1000px">
      <div class="page-header"><h1>Import / export</h1></div>
      @if (result(); as r) { <div class="info-box" style="margin-bottom:12px">Created {{ r.created }}, updated {{ r.updated }}, skipped {{ r.skipped }}.
        @if (r.openingStockDocumentId) { Opening stock posted. }</div> }
      <div class="cols">
        <div class="card"><header><h2>Products</h2></header><section class="stack">
          <div class="toolbar"><button class="btn" (click)="dl('/tools/products/export', { template: true }, 'products-template.xlsx')">Template</button>
            <button class="btn" (click)="dl('/tools/products/export', {}, 'products.xlsx')">Export all</button></div>
          <input type="file" accept=".xlsx" (change)="pf = $any($event.target).files?.[0] ?? null" />
          <label class="check"><input type="checkbox" [(ngModel)]="openingStock" /> Post the OpeningQty column as opening stock</label>
          <div><button class="btn primary" [disabled]="!pf" (click)="up('/tools/products/import', pf!, { openingStock })">Import products</button></div>
          <p class="muted small">Upserts by code; categories and manufacturers are created by name. Any bad row stops the import and nothing is saved.</p>
        </section></div>
        <div class="card"><header><h2>Customers &amp; suppliers</h2></header><section class="stack">
          <label class="field"><span>Type</span><select [(ngModel)]="ledgerType"><option value="Customer">Customers</option><option value="Supplier">Suppliers</option></select></label>
          <div class="toolbar"><button class="btn" (click)="dl('/tools/ledgers/export', { type: ledgerType, template: true }, 'ledgers-template.xlsx')">Template</button>
            <button class="btn" (click)="dl('/tools/ledgers/export', { type: ledgerType }, 'ledgers.xlsx')">Export all</button></div>
          <input type="file" accept=".xlsx" (change)="lf = $any($event.target).files?.[0] ?? null" />
          <div><button class="btn primary" [disabled]="!lf" (click)="up('/tools/ledgers/import', lf!, { type: ledgerType })">Import</button></div>
        </section></div>
        <div class="card"><header><h2>Weighing-scale PLU</h2></header><section class="stack">
          <div><button class="btn" (click)="dl('/tools/plu/export', {}, 'scale-plu.csv')">Export PLU file (CSV)</button></div>
          <input type="file" accept=".csv" (change)="uf = $any($event.target).files?.[0] ?? null" />
          <div><button class="btn primary" [disabled]="!uf" (click)="up('/tools/plu/import', uf!, {})">Import PLU codes</button></div>
          <p class="muted small">CSV columns: Code, PLU[, Price]</p>
        </section></div>
      </div>
    </div>`,
})
export class ImportExportComponent {
  private api = inject(Api);
  private ui = inject(Ui);
  result = signal<ImportResult | null>(null);
  pf: File | null = null;
  lf: File | null = null;
  uf: File | null = null;
  openingStock = false;
  ledgerType = 'Customer';
  async dl(url: string, q: Record<string, any>, name: string): Promise<void> {
    try { await this.api.download(url, undefined, q, name); } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async up(url: string, f: File, q: Record<string, any>): Promise<void> {
    const form = new FormData(); form.append('file', f);
    try { this.result.set(await this.ui.run(() => this.api.upload<ImportResult>(url, form, q))); this.ui.ok('Import complete.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

@Component({
  selector: 'app-transfer',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:1000px">
      <div class="page-header"><h1>Copy to another company</h1></div>
      <div class="card"><section class="form-grid cols-4">
        <label class="field span-2"><span>Target company</span><select [(ngModel)]="target"><option [ngValue]="null">Choose…</option>
          @for (c of companies(); track c.id) { @if (c.id !== auth.session()?.company?.id) { <option [ngValue]="c.id">{{ c.name }}</option> } }</select></label>
        <label class="check"><input type="checkbox" [(ngModel)]="allProducts" /> All items</label>
        <label class="check"><input type="checkbox" [(ngModel)]="allLedgers" /> All ledgers / parties</label>
      </section></div>
      <div class="card" style="margin-top:12px"><header><h2>Documents to copy (optional)</h2>
        <select [(ngModel)]="type" style="width:160px">@for (t of types; track t) {<option [value]="t">{{ labels[t] }}</option>}</select>
        <input type="date" [(ngModel)]="from" style="width:150px" /><input type="date" [(ngModel)]="to" style="width:150px" /><button class="btn" (click)="loadDocs()">Find</button></header>
        <table class="grid"><thead><tr><th></th><th>No</th><th>Party</th><th class="num">Amount</th></tr></thead>
          <tbody>@for (d of docs(); track d.id) { <tr><td><input type="checkbox" [(ngModel)]="picked[d.id]" /></td><td>{{ d.documentNo }}</td><td>{{ d.partyName }}</td><td class="num">{{ d.billAmount }}</td></tr> }</tbody></table>
      </div>
      <div class="toolbar" style="margin-top:12px"><button class="btn primary" (click)="run()" [disabled]="!target">Copy</button>
        <span class="muted small">Items and parties are matched by code; documents are re-posted in the target company in one transaction there.</span></div>
    </div>`,
})
export class TransferComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  labels = VOUCHER_LABELS;
  types = ['Sales', 'Purchase', 'SalesReturn', 'PurchaseReturn', 'OpeningStock'] as const;
  companies = signal<CompanyDto[]>([]);
  docs = signal<DocumentListItem[]>([]);
  picked: Record<number, boolean> = {};
  target: number | null = null;
  allProducts = true;
  allLedgers = false;
  type = 'Sales';
  from = monthStart();
  to = today();
  async ngOnInit(): Promise<void> { this.companies.set(await this.api.get<CompanyDto[]>('/company/mine')); }
  async loadDocs(): Promise<void> {
    try { this.docs.set((await this.api.get<Paged<DocumentListItem>>('/documents', { type: this.type, from: this.from, to: this.to, cancelled: false, pageSize: 500 })).items); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async run(): Promise<void> {
    const ids = Object.entries(this.picked).filter(([, v]) => v).map(([k]) => +k);
    try {
      const r = await this.ui.run(() => this.api.post<{ products: number; ledgers: number; documents: number }>('/tools/transfer',
        { targetCompanyId: this.target, productIds: [], ledgerIds: [], documentIds: ids, allProducts: this.allProducts, allLedgers: this.allLedgers }));
      this.ui.ok(`Copied ${r.products} items, ${r.ledgers} ledgers, ${r.documents} documents.`);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

@Component({
  selector: 'app-reset',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:700px">
      <div class="page-header"><h1>Reset company transactions</h1></div>
      <div class="warn-box" style="margin-bottom:12px">This permanently deletes every bill, purchase, voucher, stock movement and posting of
        <b>{{ auth.session()?.company?.name }}</b>. Items, parties, ledgers and settings are kept. Take a backup first.</div>
      @if (!code()) {
        <button class="btn danger" (click)="request()" [disabled]="!auth.isAdmin()">Step 1 — request confirmation code</button>
      } @else {
        <div class="card"><section class="stack">
          <div>Confirmation code (valid 10 minutes): <b class="mono" style="font-size:18px">{{ code() }}</b></div>
          <label class="field"><span>Type the code</span><input [(ngModel)]="typedCode" /></label>
          <label class="field"><span>Type the company name exactly: {{ companyName() }}</span><input [(ngModel)]="typedName" /></label>
          <div><button class="btn danger" (click)="confirm()" [disabled]="!typedCode || !typedName">Step 2 — delete all transactions</button></div>
          <p class="muted small">You will be asked for your password once more.</p>
        </section></div>
      }
    </div>`,
})
export class ResetComponent {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private router = inject(Router);
  code = signal('');
  companyName = signal('');
  typedCode = '';
  typedName = '';
  async request(): Promise<void> {
    try { const r = await this.api.post<{ code: string; companyName: string }>('/tools/reset/request'); this.code.set(r.code); this.companyName.set(r.companyName); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async confirm(): Promise<void> {
    try {
      await this.api.post('/tools/reset/confirm', { code: this.typedCode, companyName: this.typedName });
      this.ui.ok('All transactions were deleted.');
      this.router.navigate(['/dashboard']);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}
