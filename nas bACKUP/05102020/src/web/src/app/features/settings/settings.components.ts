import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { GstinErrorPipe } from '../../core/gstin';
import { DmyhmPipe, DmyPipe, MoneyPipe, monthStart, today } from '../../core/format';
import { CompanyDto, LicenseStatus, Paged, Right, VOUCHER_LABELS, VoucherType } from '../../core/models';
import { Theme, ThemeMode } from '../../core/theme';
import { Ui } from '../../core/ui';
import { AuthImageDirective, ImageCache } from '../../shared/auth-image';
import { ModalComponent, PagerComponent } from '../../shared/components';
import { STATE_CODES } from '../masters/masters.components';

/* ================= Appearance (per-user theme) ================= */

@Component({
  selector: 'app-appearance',
  standalone: true,
  template: `
    <div class="page" style="max-width:760px">
      <div class="page-header"><h1>Appearance</h1></div>
      <div class="card">
        <header><h2>Theme</h2><span class="muted small">Saved to your login — it follows you to every counter.</span></header>
        <section>
          <div class="theme-choices" role="radiogroup" aria-label="Theme">
            @for (o of options; track o.mode) {
              <button type="button" class="theme-choice" role="radio" [attr.aria-checked]="theme.mode() === o.mode"
                      [class.selected]="theme.mode() === o.mode" [disabled]="busy()" (click)="pick(o.mode)">
                <span [class]="'theme-preview ' + o.mode"><i></i><b></b><b></b></span>
                <span class="t-label">{{ o.label }}</span>
                <span class="muted small">{{ o.hint }}</span>
              </button>
            }
          </div>
        </section>
      </div>
    </div>`,
})
export class AppearanceComponent {
  theme = inject(Theme);
  private ui = inject(Ui);
  busy = signal(false);
  options: { mode: ThemeMode; label: string; hint: string }[] = [
    { mode: 'light', label: 'Light', hint: 'Bright screens, daytime use' },
    { mode: 'dark', label: 'Dark', hint: 'Easier on the eyes in low light' },
    { mode: 'ocean', label: 'Ocean', hint: 'Fresh teal and aqua' },
    { mode: 'sunset', label: 'Sunset', hint: 'Warm coral and amber' },
    { mode: 'system', label: 'Same as Windows', hint: 'Follows the computer setting' },
  ];

  async pick(mode: ThemeMode): Promise<void> {
    if (mode === this.theme.mode()) return;
    this.busy.set(true);
    try { await this.theme.set(mode); this.ui.ok('Theme saved.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
    finally { this.busy.set(false); }
  }
}

/* ================= Company profile ================= */

@Component({
  selector: 'app-company-profile',
  standalone: true,
  imports: [FormsModule, GstinErrorPipe, AuthImageDirective],
  template: `
    <div class="page" style="max-width:980px">
      <div class="page-header"><h1>Company profile</h1><button class="btn primary" (click)="save()" [disabled]="!auth.can('Company', 'Edit')">Save</button></div>
      @if (c(); as c) {
        <div class="card"><section class="form-grid cols-3">
          <label class="field req"><span>Company name</span><input [(ngModel)]="c.name" /></label>
          <label class="field span-2"><span>Legal name (on invoices)</span><input [(ngModel)]="c.legalName" /></label>
          <label class="field span-2"><span>Address line 1</span><input [(ngModel)]="c.address1" /></label>
          <label class="field"><span>Address line 2</span><input [(ngModel)]="c.address2" /></label>
          <label class="field"><span>City</span><input [(ngModel)]="c.city" /></label>
          <label class="field req"><span>State</span><select [(ngModel)]="c.stateCode">@for (s of states; track s[0]) {<option [value]="s[0]">{{ s[0] }} {{ s[1] }}</option>}</select></label>
          <label class="field"><span>PIN code</span><input [(ngModel)]="c.postalCode" /></label>
          <label class="field"><span>GSTIN</span><input [(ngModel)]="c.gstin" maxlength="20" style="text-transform:uppercase" />
            @if (c.gstin | gstinError: c.stateCode; as err) { <small class="danger-text">{{ err }}</small> }</label>
          <label class="field"><span>PAN</span><input [(ngModel)]="c.pan" maxlength="10" /></label>
          <label class="field"><span>Phone</span><input [(ngModel)]="c.phone" /></label>
          <label class="field"><span>E-mail</span><input [(ngModel)]="c.email" /></label>
          <label class="field span-2"><span>Website</span><input [(ngModel)]="c.website" /></label>
          <label class="field span-all"><span>Invoice footer text</span><textarea [(ngModel)]="c.invoiceFooter"></textarea></label>
        </section></div>
        <div class="card" style="margin-top:12px"><header><h2>Logo</h2></header><section class="row center-y">
          <img [appAuthSrc]="'/api/company/logo?v=' + logoVer()" (load)="noLogo.set(false)" alt="Company logo" style="max-height:80px;max-width:220px" (error)="noLogo.set(true)" [hidden]="noLogo()" />
          @if (noLogo()) { <span class="muted">No logo</span> }
          <input type="file" accept="image/png,image/jpeg" (change)="logo($event)" style="width:auto" />
          <button class="btn" (click)="clearLogo()">Remove</button>
        </section></div>
      }
    </div>`,
})
export class CompanyProfileComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  states = STATE_CODES;
  c = signal<CompanyDto | null>(null);
  noLogo = signal(false);
  logoVer = signal(0);
  private images = inject(ImageCache);
  async ngOnInit(): Promise<void> { this.c.set(await this.api.get<CompanyDto>('/company')); }
  async save(): Promise<void> {
    try { await this.api.put('/company', { ...this.c(), gstin: this.c()!.gstin?.trim().toUpperCase() || null }); await this.auth.loadSession(); this.ui.ok('Company saved.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async logo(e: Event): Promise<void> {
    const f = (e.target as HTMLInputElement).files?.[0];
    if (!f) return;
    const form = new FormData(); form.append('file', f);
    try {
      await this.api.upload('/company/logo', form, undefined, 'PUT');
      this.images.invalidate('/api/company/logo');
      this.noLogo.set(false); this.logoVer.update(v => v + 1);
    } catch (err) { this.ui.error(errorMessage(err)); }
    (e.target as HTMLInputElement).value = '';
  }
  async clearLogo(): Promise<void> {
    try {
      await this.api.upload('/company/logo', new FormData(), undefined, 'PUT');
      this.images.invalidate('/api/company/logo');
      this.noLogo.set(true); this.logoVer.update(v => v + 1);
    } catch (err) { this.ui.error(errorMessage(err)); }
  }
}

/* ================= Feature settings (generated from server definitions) ================= */

interface SettingDef { key: string; label: string; group: string; type: 'Bool' | 'Int' | 'Decimal' | 'Text' | 'Choice' | 'Date'; default: string; options?: string[]; help?: string; }

@Component({
  selector: 'app-features',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:1100px">
      <div class="page-header"><h1>Features &amp; settings</h1><button class="btn primary" (click)="save()" [disabled]="!auth.can('Settings', 'Edit')">Save</button></div>
      <div class="cols">
        @for (g of groups(); track g.name) {
          <div class="card"><header><h2>{{ g.name }}</h2></header>
            <section class="stack">
              @for (d of g.items; track d.key) {
                @switch (d.type) {
                  @case ('Bool') { <label class="check"><input type="checkbox" [ngModel]="values[d.key] === 'true'" (ngModelChange)="values[d.key] = $event ? 'true' : 'false'" /> {{ d.label }}</label> }
                  @case ('Choice') { <label class="field"><span>{{ d.label }}</span><select [(ngModel)]="values[d.key]">@for (o of d.options; track o) {<option [value]="o">{{ o }}</option>}</select></label> }
                  @case ('Date') { <label class="field"><span>{{ d.label }}</span><input type="date" [(ngModel)]="values[d.key]" /></label> }
                  @case ('Text') { <label class="field"><span>{{ d.label }}</span><input [(ngModel)]="values[d.key]" /></label> }
                  @default { <label class="field"><span>{{ d.label }}</span><input type="number" class="num" [(ngModel)]="values[d.key]" /></label> }
                }
              }
            </section>
          </div>
        }
      </div>
      <p class="muted small">SMS gateway and e-mail credentials are configured by the operator in server secrets (Messaging:Sms / Messaging:Email), never here.</p>
    </div>`,
})
export class FeaturesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  defs = signal<SettingDef[]>([]);
  values: Record<string, string> = {};
  groups = computed(() => {
    const m = new Map<string, SettingDef[]>();
    for (const d of this.defs()) { if (!m.has(d.group)) m.set(d.group, []); m.get(d.group)!.push(d); }
    return [...m.entries()].map(([name, items]) => ({ name, items }));
  });
  async ngOnInit(): Promise<void> {
    const r = await this.api.get<{ definitions: SettingDef[]; values: Record<string, string> }>('/company/settings');
    this.values = { ...r.values };
    this.defs.set(r.definitions);
  }
  async save(): Promise<void> {
    try { await this.api.put('/company/settings', Object.fromEntries(Object.entries(this.values).map(([k, v]) => [k, String(v ?? '')]))); await this.auth.loadSession(); this.ui.ok('Settings saved.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Per-voucher screen options ================= */

const SCREEN_TYPES: VoucherType[] = ['POSSales', 'Sales', 'LocalSales', 'SalesReturn', 'Estimate', 'SalesOrder', 'DeliveryNote', 'Purchase', 'LocalPurchase',
  'PurchaseReturn', 'PurchaseOrder', 'ReceiptNote', 'OpeningStock', 'StockAdjustment', 'Production'];

@Component({
  selector: 'app-screen-options',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:900px">
      <div class="page-header"><h1>Screen options</h1>
        <select [(ngModel)]="type" (ngModelChange)="load()" style="width:200px">@for (t of types; track t) {<option [value]="t">{{ labels[t] }}</option>}</select>
        <button class="btn primary" (click)="save()" [disabled]="!auth.can('Settings', 'Edit')">Save</button></div>
      @if (o(); as o) {
        <div class="card"><section class="form-grid cols-2">
          @for (f of flags; track f[0]) { <label class="check"><input type="checkbox" [(ngModel)]="o[f[0]]" /> {{ f[1] }}</label> }
          <label class="field"><span>Extra column name</span><input [(ngModel)]="o.extraColumnName" /></label>
          <label class="field"><span>Print scheme</span><select [(ngModel)]="o.printSchemeId"><option [ngValue]="null">Default</option>
            @for (s of schemes(); track s.id) {<option [ngValue]="s.id">{{ s.name }} ({{ s.format }})</option>}</select></label>
          <label class="field"><span>Amount decimals</span><input type="number" class="num" [(ngModel)]="o.amountDecimals" /></label>
          <label class="field"><span>Quantity decimals</span><input type="number" class="num" [(ngModel)]="o.qtyDecimals" /></label>
        </section></div>
      }
    </div>`,
})
export class ScreenOptionsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  labels = VOUCHER_LABELS;
  types = SCREEN_TYPES;
  type: VoucherType = 'POSSales';
  o = signal<Record<string, any> | null>(null);
  schemes = signal<{ id: number; name: string; format: string }[]>([]);
  flags: [string, string][] = [['printOnSave', 'Print on save'], ['confirmPrint', 'Ask before printing'], ['showPreview', 'Preview instead of direct print'],
    ['inclusiveUnitPrice', 'Unit price includes tax'], ['freeQty', 'Free quantity column'], ['extraColumn', 'Extra column'], ['productRemarks', 'Product remarks'],
    ['disableRateEdit', 'Disable rate editing'], ['scannerMode', 'Scanner mode (increment on scan)'], ['cashDeskOnSave', 'Cash desk on save'],
    ['focusNextRow', 'Focus next row after entry'], ['customerAutoSave', 'Quick-create customers from the bill'], ['deliveryDate', 'Delivery date field'],
    ['sendSms', 'Send bill SMS']];
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> {
    const [o, s] = await Promise.all([this.api.get<Record<string, any>>(`/company/invoice-options/${this.type}`), this.api.get<any[]>('/print/schemes', { type: this.type })]);
    this.o.set(o); this.schemes.set(s);
  }
  async save(): Promise<void> {
    try { await this.api.put(`/company/invoice-options/${this.type}`, this.o()); this.ui.ok('Saved.'); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Numbering ================= */

interface Seq { id: number; voucherType: VoucherType; name: string; prefix: string; nextNumber: number; }

@Component({
  selector: 'app-numbering',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:760px">
      <div class="page-header"><h1>Document numbering · FY {{ auth.session()?.financialYear?.name }}</h1><button class="btn primary" (click)="save()" [disabled]="!auth.can('Settings', 'Edit')">Save</button></div>
      <div class="info-box" style="margin-bottom:10px">Numbers are allocated atomically on the server at save time. You can change prefixes or move the next number forward, never backwards.</div>
      <table class="grid">
        <thead><tr><th>Voucher</th><th style="width:160px">Prefix</th><th class="num" style="width:160px">Next number</th><th>Example</th></tr></thead>
        <tbody>@for (s of rows(); track s.voucherType) {
          <tr><td>{{ s.name }}</td><td><input [(ngModel)]="s.prefix" /></td><td><input type="number" class="num" [(ngModel)]="s.nextNumber" /></td><td class="mono">{{ s.prefix }}{{ s.nextNumber }}</td></tr>
        }</tbody>
      </table>
    </div>`,
})
export class NumberingComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<Seq[]>([]);
  async ngOnInit(): Promise<void> { this.rows.set(await this.api.get<Seq[]>('/company/sequences')); }
  async save(): Promise<void> {
    try { await this.api.put('/company/sequences', this.rows()); this.ui.ok('Saved.'); this.rows.set(await this.api.get<Seq[]>('/company/sequences')); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Print schemes (template designer) ================= */

interface Scheme { id: number; voucherType: VoucherType; name: string; format: string; isDefault: boolean; textMode: boolean; template: any; }
const COLUMNS = ['SlNo', 'Item', 'Hsn', 'Batch', 'Qty', 'Mrp', 'Rate', 'Disc', 'Taxable', 'Gst', 'Cgst', 'Sgst', 'Igst', 'Total'];

@Component({
  selector: 'app-print-schemes',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <div class="page-header"><h1>Print schemes</h1>
        <select [(ngModel)]="type" (ngModelChange)="load()" style="width:200px">@for (t of types; track t) {<option [value]="t">{{ labels[t] }}</option>}</select>
        <button class="btn" (click)="newScheme()">New scheme</button>
        @if (cur()) { <button class="btn primary" (click)="save()">Save</button> }</div>
      <div class="row">
        <div class="card" style="width:240px"><section class="stack" style="gap:4px">
          @for (s of list(); track s.id) { <button class="link-btn" style="text-align:left" (click)="cur.set(clone(s))">{{ s.name }} <span class="badge">{{ s.format }}</span> @if (s.isDefault) {<span class="badge ok">default</span>}</button> }
        </section></div>
        @if (cur(); as s) {
          <div class="card grow"><section class="stack">
            <div class="form-grid cols-4">
              <label class="field span-2"><span>Name</span><input [(ngModel)]="s.name" /></label>
              <label class="field"><span>Paper</span><select [(ngModel)]="s.format"><option value="A4">A4</option><option value="A5">A5</option><option value="Thermal80">Thermal 80 mm</option><option value="Thermal58">Thermal 58 mm</option></select></label>
              <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="s.isDefault" /> Default</label>
              <label class="field span-2"><span>Title</span><input [(ngModel)]="s.template.title" /></label>
              <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="s.textMode" /> Text mode (raw thermal)</label>
            </div>
            <div class="form-grid cols-4">
              @for (f of toggles; track f[0]) { <label class="check"><input type="checkbox" [(ngModel)]="s.template[f[0]]" /> {{ f[1] }}</label> }
            </div>
            <div><b>Item grid columns</b>
              <div class="toolbar" style="margin-top:6px">@for (c of columns; track c) {
                <label class="check"><input type="checkbox" [ngModel]="s.template.columns.includes(c)" (ngModelChange)="toggleCol(s, c, $event)" /> {{ c }}</label> }</div></div>
            <label class="field"><span>Header lines (one per line)</span><textarea [ngModel]="s.template.headerLines.join('\\n')" (ngModelChange)="s.template.headerLines = splitLines($event)"></textarea></label>
            <label class="field"><span>Footer lines (one per line)</span><textarea [ngModel]="s.template.footerLines.join('\\n')" (ngModelChange)="s.template.footerLines = splitLines($event)"></textarea></label>
            @if (s.id && !s.isDefault) { <div><button class="btn danger" (click)="remove(s)">Delete scheme</button></div> }
          </section></div>
        }
      </div>
    </div>`,
})
export class PrintSchemesComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  labels = VOUCHER_LABELS;
  types: VoucherType[] = [...SCREEN_TYPES, 'Receipt', 'Payment'];
  type: VoucherType = 'Sales';
  columns = COLUMNS;
  toggles: [string, string][] = [['showLogo', 'Logo'], ['showCompanyAddress', 'Company address'], ['showGstin', 'GSTIN'], ['showParty', 'Party block'],
    ['showTaxSummary', 'Tax summary'], ['showAmountInWords', 'Amount in words'], ['showYouSaved', '"You saved"'], ['showRegionalName', 'Regional item names'], ['showSignature', 'Signature']];
  list = signal<Scheme[]>([]);
  cur = signal<Scheme | null>(null);
  clone = (s: Scheme): Scheme => JSON.parse(JSON.stringify(s));
  splitLines = (v: string) => v.split('\n').map(x => x.trim()).filter(Boolean);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> {
    const l = await this.api.get<Scheme[]>('/print/schemes', { type: this.type });
    this.list.set(l);
    this.cur.set(l.length ? this.clone(l[0]) : null);
  }
  newScheme(): void {
    const base = this.list()[0];
    this.cur.set({ id: 0, voucherType: this.type, name: 'New scheme', format: 'A4', isDefault: false, textMode: false,
      template: base ? this.clone(base).template : { title: 'TAX INVOICE', columns: ['SlNo', 'Item', 'Qty', 'Rate', 'Total'], headerLines: [], footerLines: [] } });
  }
  toggleCol(s: Scheme, c: string, on: boolean): void {
    const cols: string[] = s.template.columns.filter((x: string) => x !== c);
    if (on) cols.push(c);
    s.template.columns = COLUMNS.filter(x => cols.includes(x));
  }
  async save(): Promise<void> {
    const s = this.cur()!;
    try { s.id ? await this.api.put(`/print/schemes/${s.id}`, s) : await this.api.post('/print/schemes', s); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(s: Scheme): Promise<void> {
    try { await this.api.delete(`/print/schemes/${s.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Messaging ================= */

interface Template { id: number; eventId?: number | null; eventName?: string; name: string; channel: 'Sms' | 'Email'; subject?: string; body: string; isActive: boolean; }
interface Outbox { id: number; channel: string; recipient: string; subject?: string; body: string; status: string; attempts: number; lastError?: string; createdAt: string; sentAt?: string; source?: string; }

@Component({
  selector: 'app-messaging',
  standalone: true,
  imports: [FormsModule, ModalComponent, DmyhmPipe],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>SMS &amp; e-mail</h1>
        @if (!auth.flag('EnableSms')) { <span class="badge warn">SMS disabled in Features</span> }</div>
      <div class="tabs">@for (t of ['Templates', 'Bulk / scheduled', 'Outbox']; track t) { <button [class.active]="tab() === t" (click)="tab.set(t); load()">{{ t }}</button> }</div>
      @if (tab() === 'Templates') {
        <div class="toolbar" style="margin-bottom:8px"><button class="btn primary" (click)="edit(null)">New template</button>
          <span class="muted small">Tags: @for (t of tags(); track t) { <code>{{ '{' + t + '}' }}</code> }</span></div>
        <table class="grid">
          <thead><tr><th>Name</th><th>Event</th><th>Channel</th><th>Message</th><th>Active</th></tr></thead>
          <tbody>@for (t of templates(); track t.id) {
            <tr class="clickable" (click)="edit(t)"><td><b>{{ t.name }}</b></td><td>{{ t.eventName }}</td><td>{{ t.channel }}</td><td class="muted">{{ t.body }}</td><td>{{ t.isActive ? 'Yes' : '' }}</td></tr>
          }</tbody>
        </table>
      }
      @if (tab() === 'Bulk / scheduled') {
        <div class="card"><section class="form-grid cols-4">
          <label class="field"><span>Template</span><select [(ngModel)]="bulk.templateId">@for (t of templates(); track t.id) {<option [ngValue]="t.id">{{ t.name }}</option>}</select></label>
          <label class="field"><span>Send at (empty = now)</span><input type="datetime-local" [(ngModel)]="bulk.at" /></label>
          <label class="field span-2"><span>Customers</span><div class="row"><input [(ngModel)]="custSearch" placeholder="Filter customers" (keydown.enter)="loadCustomers()" /><button class="btn" (click)="loadCustomers()">Find</button></div></label>
        </section></div>
        <table class="grid" style="margin-top:10px">
          <thead><tr><th style="width:40px"><input type="checkbox" (change)="selectAll($event)" /></th><th>Customer</th><th>Mobile</th><th>E-mail</th></tr></thead>
          <tbody>@for (c of customers(); track c.id) {
            <tr><td><input type="checkbox" [(ngModel)]="picked[c.id]" /></td><td>{{ c.name }}</td><td>{{ c.mobile }}</td><td>{{ c.email }}</td></tr>
          }</tbody>
        </table>
        <div class="toolbar" style="margin-top:8px"><button class="btn primary" (click)="sendBulk()">Send / schedule</button></div>
      }
      @if (tab() === 'Outbox') {
        <table class="grid">
          <thead><tr><th>Created</th><th>Channel</th><th>To</th><th>Source</th><th>Message</th><th>Status</th><th>Error</th><th></th></tr></thead>
          <tbody>@for (m of outbox(); track m.id) {
            <tr><td>{{ m.createdAt | dmyhm }}</td><td>{{ m.channel }}</td><td>{{ m.recipient }}</td><td>{{ m.source }}</td><td class="muted">{{ m.subject || m.body }}</td>
              <td><span class="badge" [class.ok]="m.status === 'Sent'" [class.danger]="m.status === 'Failed'">{{ m.status }}</span></td><td class="danger-text small">{{ m.lastError }}</td>
              <td class="actions">@if (m.status === 'Failed') {<button class="btn sm" (click)="retry(m)">Retry</button>}</td></tr>
          } @empty { <tr><td colspan="8" class="empty">No messages.</td></tr> }</tbody>
        </table>
      }
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit template' : 'New template'" (closed)="form.set(null)">
        <div class="form-grid cols-2">
          <label class="field"><span>Name</span><input [(ngModel)]="f.name" /></label>
          <label class="field"><span>Event</span><select [(ngModel)]="f.eventId"><option [ngValue]="null">—</option>@for (e of events(); track e.id) {<option [ngValue]="e.id">{{ e.name }}</option>}</select></label>
          <label class="field"><span>Channel</span><select [(ngModel)]="f.channel"><option value="Sms">SMS</option><option value="Email">E-mail</option></select></label>
          @if (f.channel === 'Email') { <label class="field"><span>Subject</span><input [(ngModel)]="f.subject" /></label> }
          <label class="field span-2"><span>Message</span><textarea rows="5" [(ngModel)]="f.body"></textarea></label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        @if (f.id) { <button footer class="btn danger" (click)="removeTemplate(f)">Delete</button> }
        <button footer class="btn primary" (click)="saveTemplate(f)">Save</button>
      </app-modal>
    }`,
})
export class MessagingComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  tab = signal('Templates');
  templates = signal<Template[]>([]);
  events = signal<{ id: number; name: string }[]>([]);
  tags = signal<string[]>([]);
  outbox = signal<Outbox[]>([]);
  customers = signal<{ id: number; name: string; mobile?: string; email?: string }[]>([]);
  form = signal<Template | null>(null);
  picked: Record<number, boolean> = {};
  custSearch = '';
  bulk = { templateId: null as number | null, at: '' };
  async ngOnInit(): Promise<void> {
    const [e, t] = await Promise.all([this.api.get<any[]>('/lookups/smsevent'), this.api.get<string[]>('/messaging/tags')]);
    this.events.set(e); this.tags.set(t);
    await this.load();
  }
  async load(): Promise<void> {
    try {
      if (this.tab() === 'Outbox') this.outbox.set(await this.api.get<Outbox[]>('/messaging/outbox'));
      else { this.templates.set(await this.api.get<Template[]>('/messaging/templates')); this.bulk.templateId ??= this.templates()[0]?.id ?? null; }
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  edit(t: Template | null): void { this.form.set(t ? { ...t } : { id: 0, name: '', channel: 'Sms', body: '', isActive: true, eventId: null }); }
  async saveTemplate(f: Template): Promise<void> {
    try { f.id ? await this.api.put(`/messaging/templates/${f.id}`, f) : await this.api.post('/messaging/templates', f); this.form.set(null); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async removeTemplate(f: Template): Promise<void> { try { await this.api.delete(`/messaging/templates/${f.id}`); this.form.set(null); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); } }
  async loadCustomers(): Promise<void> {
    const r = await this.api.get<Paged<any>>('/ledgers', { partyType: 'Customer', search: this.custSearch, active: true, pageSize: 500 });
    this.customers.set(r.items);
  }
  selectAll(e: Event): void { const on = (e.target as HTMLInputElement).checked; for (const c of this.customers()) this.picked[c.id] = on; }
  async sendBulk(): Promise<void> {
    const ids = Object.entries(this.picked).filter(([, v]) => v).map(([k]) => +k);
    if (!ids.length || !this.bulk.templateId) { this.ui.error('Choose a template and customers.'); return; }
    try {
      const r = await this.api.post<{ count: number }>('/messaging/bulk', { templateId: this.bulk.templateId, ledgerIds: ids, scheduleAt: this.bulk.at ? new Date(this.bulk.at).toISOString() : null });
      this.ui.ok(`${r.count} messages queued.`);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async retry(m: Outbox): Promise<void> { await this.api.post(`/messaging/outbox/${m.id}/retry`); await this.load(); }
}

/* ================= Users ================= */

interface UserRow { id: number; userName: string; displayName: string; email?: string; isActive: boolean; roleId?: number | null; roleName?: string; password?: string | null; lockoutEnd?: string | null; }

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [FormsModule, ModalComponent],
  template: `
    <div class="page" style="max-width:1000px">
      <div class="page-header"><h1>Users @if (license(); as l) { <span class="muted">{{ l.usersInUse }} of {{ l.maxUsers ?? 'unlimited' }} active</span> }</h1>
        @if (auth.can('Users', 'Add')) {<button class="btn primary" (click)="edit(null)">New user</button>}</div>
      @if (license(); as l) {
        @if (l.maxUsers != null && l.usersInUse >= l.maxUsers) {
          <div class="warn-box" style="margin-bottom:12px"><div>All {{ l.maxUsers }} user places in your subscription are in use. To add someone, deactivate a user you no longer need,
            or contact NAS POS support for a new subscription with more users.</div></div>
        }
      }
      <table class="grid">
        <thead><tr><th>User name</th><th>Display name</th><th>E-mail</th><th>Role</th><th>Status</th></tr></thead>
        <tbody>@for (u of rows(); track u.id) {
          <tr class="clickable" [class.cancelled]="!u.isActive" (click)="edit(u)"><td><b>{{ u.userName }}</b></td><td>{{ u.displayName }}</td><td>{{ u.email }}</td><td>{{ u.roleName }}</td>
            <td>{{ u.isActive ? 'Active' : 'Disabled' }} @if (u.lockoutEnd) {<span class="badge warn">locked</span>}</td></tr>
        }</tbody>
      </table>
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit user' : 'New user'" size="sm" (closed)="form.set(null)">
        <div class="stack">
          <label class="field req"><span>User name</span><input [(ngModel)]="f.userName" autocomplete="off" /></label>
          <label class="field"><span>Display name</span><input [(ngModel)]="f.displayName" /></label>
          <label class="field"><span>E-mail</span><input [(ngModel)]="f.email" /></label>
          <label class="field req"><span>Role</span><select [(ngModel)]="f.roleId">@for (r of roles(); track r.id) {<option [ngValue]="r.id">{{ r.name }}</option>}</select></label>
          <label class="field" [class.req]="!f.id"><span>{{ f.id ? 'New password (leave blank to keep)' : 'Password' }}</span><input type="password" [(ngModel)]="f.password" autocomplete="new-password" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        @if (f.id && auth.can('Users', 'Delete')) { <button footer class="btn danger" (click)="remove(f)">Remove from company</button> }
        <button footer class="btn primary" (click)="save(f)">Save</button>
      </app-modal>
    }`,
})
export class UsersComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<UserRow[]>([]);
  roles = signal<{ id: number; name: string }[]>([]);
  form = signal<UserRow | null>(null);
  async ngOnInit(): Promise<void> {
    if (this.auth.can('Roles')) this.roles.set(await this.api.get<any[]>('/roles'));
    await this.load();
  }
  license = signal<LicenseStatus | null>(null);
  async load(): Promise<void> {
    this.rows.set(await this.api.get<UserRow[]>('/users'));
    try { this.license.set(await this.api.get<LicenseStatus>('/license')); } catch { /* the limit is also enforced on save */ }
  }
  edit(u: UserRow | null): void { this.form.set(u ? { ...u, password: '' } : { id: 0, userName: '', displayName: '', isActive: true, roleId: this.roles()[0]?.id, password: '' }); }
  async save(f: UserRow): Promise<void> {
    try { const body = { ...f, password: f.password || null }; f.id ? await this.api.put(`/users/${f.id}`, body) : await this.api.post('/users', body); this.form.set(null); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(f: UserRow): Promise<void> {
    if (!(await this.ui.confirm('Remove user', `Remove ${f.userName} from this company?`, { danger: true }))) return;
    try { await this.api.delete(`/users/${f.id}`); this.form.set(null); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Roles & permission matrix ================= */

interface Role { id: number; name: string; isAdmin: boolean; isActive: boolean; permissions: { screen: string; rights: number }[]; userCount: number; }
const RIGHT_NAMES = ['Read', 'Add', 'Edit', 'Cancel', 'Delete', 'Print', 'Export', 'Email', 'Sms'] as const;

@Component({
  selector: 'app-roles',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Roles &amp; rights</h1>
        <select [ngModel]="cur()?.id" (ngModelChange)="pick($event)" style="width:220px">@for (r of roles(); track r.id) {<option [ngValue]="r.id">{{ r.name }} ({{ r.userCount }} users)</option>}</select>
        @if (auth.can('Roles', 'Add')) { <button class="btn" (click)="newRole()">New role</button> }
        @if (cur() && !cur()!.isAdmin && cur()!.id && auth.can('Roles', 'Delete')) { <button class="btn danger" (click)="remove()">Delete</button> }
        <button class="btn primary" (click)="save()" [disabled]="!cur() || cur()!.isAdmin">Save</button></div>
      @if (cur(); as r) {
        <div class="card" style="margin-bottom:10px"><section class="row center-y">
          <label class="field"><span>Role name</span><input [(ngModel)]="r.name" [disabled]="r.isAdmin" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="r.isActive" [disabled]="r.isAdmin" /> Active</label>
          @if (r.isAdmin) { <span class="badge info">The administrator role always has every right.</span> }
        </section></div>
        <div class="table-wrap" style="max-height:calc(100vh - 250px)">
          <table class="grid matrix">
            <thead><tr><th>Screen</th>@for (n of rightNames; track n) {<th><button class="link-btn" (click)="column(n)">{{ n }}</button></th>}<th>All</th></tr></thead>
            <tbody>@for (p of r.permissions; track p.screen) {
              <tr><td>{{ p.screen }}</td>
                @for (n of rightNames; track n) { <td><input type="checkbox" [checked]="has(p, n)" (change)="toggle(p, n)" [disabled]="r.isAdmin" /></td> }
                <td><input type="checkbox" [checked]="p.rights === all" (change)="p.rights = p.rights === all ? 0 : all" [disabled]="r.isAdmin" /></td></tr>
            }</tbody>
          </table>
        </div>
      }
    </div>`,
})
export class RolesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rightNames = RIGHT_NAMES;
  all = Right.All;
  roles = signal<Role[]>([]);
  cur = signal<Role | null>(null);
  async ngOnInit(): Promise<void> { await this.load(); }
  async load(id?: number): Promise<void> {
    const r = await this.api.get<Role[]>('/roles');
    this.roles.set(r);
    this.pick(id ?? r.find(x => !x.isAdmin)?.id ?? r[0]?.id);
  }
  pick(id: number | undefined): void { const r = this.roles().find(x => x.id === id); this.cur.set(r ? JSON.parse(JSON.stringify(r)) : null); }
  has(p: { rights: number }, n: typeof RIGHT_NAMES[number]): boolean { return (p.rights & Right[n]) !== 0; }
  toggle(p: { rights: number }, n: typeof RIGHT_NAMES[number]): void { p.rights ^= Right[n]; if (p.rights && n !== 'Read') p.rights |= Right.Read; }
  column(n: typeof RIGHT_NAMES[number]): void {
    const r = this.cur(); if (!r || r.isAdmin) return;
    const on = !r.permissions.every(p => this.has(p, n));
    r.permissions.forEach(p => { p.rights = on ? p.rights | Right[n] | Right.Read : p.rights & ~Right[n]; });
  }
  async newRole(): Promise<void> {
    const screens = await this.api.get<string[]>('/roles/screens');
    this.cur.set({ id: 0, name: 'New role', isAdmin: false, isActive: true, userCount: 0, permissions: screens.map(s => ({ screen: s, rights: 0 })) });
  }
  async save(): Promise<void> {
    const r = this.cur()!;
    try { const res = r.id ? await this.api.put<{ id: number }>(`/roles/${r.id}`, r) : await this.api.post<{ id: number }>('/roles', r); this.ui.ok('Role saved.'); await this.load(res.id); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(): Promise<void> {
    if (!(await this.ui.confirm('Delete role', `Delete ${this.cur()!.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/roles/${this.cur()!.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= User action log ================= */

interface LogRow { id: number; at: string; userName: string; screen: string; action: string; remarks?: string; machine?: string; }

@Component({
  selector: 'app-audit',
  standalone: true,
  imports: [FormsModule, DmyhmPipe, PagerComponent],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>User action log</h1></div>
      <div class="card"><section class="form-grid cols-6">
        <label class="field"><span>From</span><input type="date" [(ngModel)]="f.from" /></label>
        <label class="field"><span>To</span><input type="date" [(ngModel)]="f.to" /></label>
        <label class="field"><span>User</span><input [(ngModel)]="f.user" /></label>
        <label class="field"><span>Screen</span><input [(ngModel)]="f.screen" /></label>
        <label class="field"><span>Action</span><select [(ngModel)]="f.action"><option value="">All</option>@for (a of actions; track a) {<option>{{ a }}</option>}</select></label>
        <label class="field"><span>Text</span><div class="row"><input [(ngModel)]="f.search" (keydown.enter)="load(1)" /><button class="btn" (click)="load(1)">Search</button></div></label>
      </section></div>
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th>When</th><th>User</th><th>Screen</th><th>Action</th><th>Details</th><th>Counter / IP</th></tr></thead>
          <tbody>@for (r of rows(); track r.id) {
            <tr><td class="nowrap">{{ r.at | dmyhm }}</td><td>{{ r.userName }}</td><td>{{ r.screen }}</td><td><span class="badge" [class.danger]="['Cancel', 'Delete', 'Reset', 'LoginFailed'].includes(r.action)">{{ r.action }}</span></td>
              <td>{{ r.remarks }}</td><td class="muted">{{ r.machine }}</td></tr>
          } @empty { <tr><td colspan="6" class="empty">No entries.</td></tr> }</tbody>
        </table>
      </div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="100" (go)="load($event)" />
    </div>`,
})
export class AuditComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  actions = ['Add', 'Edit', 'Cancel', 'Delete', 'Print', 'Export', 'Import', 'Login', 'LoginFailed', 'Close', 'Backup', 'Restore', 'Reset', 'ResetRequested'];
  f = { from: monthStart(), to: today(), user: '', screen: '', action: '', search: '' };
  rows = signal<LogRow[]>([]);
  total = signal(0);
  page = signal(1);
  ngOnInit(): void { this.load(1); }
  async load(p: number): Promise<void> {
    try { const r = await this.api.get<Paged<LogRow>>('/audit', { ...this.f, page: p, pageSize: 100 }); this.rows.set(r.items); this.total.set(r.total); this.page.set(p); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Subscription license ================= */

@Component({
  selector: 'app-license',
  standalone: true,
  imports: [FormsModule, DmyPipe, DmyhmPipe],
  template: `
    <div class="page" style="max-width:900px">
      <div class="page-header"><h1>License</h1></div>
      @if (status(); as s) {
        <div class="card" style="margin-bottom:16px"><header><h2>{{ auth.session()?.company?.name }}</h2>
          <span class="badge" [class.ok]="s.state === 'Active'" [class.warn]="s.state === 'ExpiringSoon' || s.state === 'Grace'"
                [class.danger]="!s.canBill">{{ stateLabel[s.state] }}</span></header>
          <section class="stack">
            <div [class.error-box]="!s.canBill" [class.warn-box]="s.canBill && s.state !== 'Active'" [class.info-box]="s.state === 'Active'"><div>{{ s.message }}</div></div>
            <div class="form-grid cols-3">
              <div><div class="muted small">Plan</div><b>{{ s.plan || '—' }}</b></div>
              <div><div class="muted small">Valid until</div><b>{{ s.plan === 'Lifetime' ? 'Lifetime' : (s.expiresOn | dmy) || '—' }}</b></div>
              <div><div class="muted small">Key ID</div><b class="mono">{{ s.keyId || '—' }}</b></div>
              <div><div class="muted small">Users</div><b [class.danger-text]="s.maxUsers != null && s.usersInUse >= s.maxUsers">
                {{ s.usersInUse }} of {{ s.maxUsers ?? 'unlimited' }}</b></div>
              @if (s.appliedAt) { <div><div class="muted small">Key entered</div>{{ s.appliedAt | dmyhm }}</div> }
              @if (s.graceUntil && s.state === 'Grace') { <div><div class="muted small">Grace period ends</div><b>{{ s.graceUntil | dmy }}</b></div> }
            </div>
            <p class="muted small">When the license ends, new bills, purchases and vouchers can't be saved. Viewing, reports, reprints and backups keep working.
              The number of active users can't go above the limit in the key — for more users, ask for a new subscription.
              To renew, contact NAS POS support with the company name{{ auth.session()?.company?.gstin ? ', GSTIN' : '' }} and Key ID shown here.</p>
          </section>
        </div>
      }
      <div class="card"><header><h2>Enter a new license key</h2></header>
        <section class="stack">
          <label class="field"><span>License key</span>
            <textarea rows="4" [(ngModel)]="key" spellcheck="false" class="mono" placeholder="NAS1-XXXXX-XXXXX-…" [disabled]="!auth.can('Company', 'Edit')"></textarea></label>
          <div class="muted small">The key must be issued for <b>{{ auth.session()?.company?.name }}</b>@if (auth.session()?.company?.gstin; as g) { (GSTIN {{ g }}, if the key carries one) }.
            Capitals, spaces and punctuation in the name don't matter.</div>
          <div><button class="btn primary" (click)="apply()" [disabled]="!key.trim() || !auth.can('Company', 'Edit')">Apply key</button>
            @if (!auth.can('Company', 'Edit')) { <span class="muted small"> Only an administrator can enter a key.</span> }</div>
        </section>
      </div>
    </div>`,
})
export class LicenseComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  status = signal<LicenseStatus | null>(null);
  key = '';
  stateLabel: Record<LicenseStatus['state'], string> = {
    Active: 'Active', ExpiringSoon: 'Ending soon', Grace: 'No key yet (grace period)', Expired: 'Expired', Missing: 'No license',
  };

  async ngOnInit(): Promise<void> {
    try { this.status.set(await this.api.get<LicenseStatus>('/license')); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async apply(): Promise<void> {
    try {
      this.status.set(await this.ui.run(() => this.api.put<LicenseStatus>('/license', { key: this.key })));
      this.key = '';
      this.ui.ok('License key applied.');
      await this.auth.loadSession(); // refresh the banner
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Companies, backup and restore ================= */

@Component({
  selector: 'app-companies',
  standalone: true,
  imports: [FormsModule, ModalComponent, GstinErrorPipe],
  template: `
    <div class="page" style="max-width:1000px">
      <div class="page-header"><h1>Companies &amp; backup</h1>
        @if (auth.can('Company', 'Add')) { <button class="btn primary" (click)="form.set(blank())">New company</button> }</div>
      <table class="grid">
        <thead><tr><th>Company</th><th>GSTIN</th><th>State</th><th>Status</th><th></th></tr></thead>
        <tbody>@for (c of list(); track c.id) {
          <tr [class.cancelled]="!c.isActive"><td><b>{{ c.name }}</b> @if (c.id === auth.session()?.company?.id) {<span class="badge info">current</span>}</td><td class="mono">{{ c.gstin }}</td>
            <td>{{ c.stateCode }}</td><td>{{ c.isActive ? 'Active' : 'Inactive' }}{{ c.isHidden ? ' · hidden' : '' }}</td>
            <td class="actions">
              @if (c.id !== auth.session()?.company?.id) { <button class="btn sm" (click)="open(c)">Open</button> }
              <button class="btn ghost sm" (click)="visibility(c, !c.isHidden, !!c.isActive)">{{ c.isHidden ? 'Unhide' : 'Hide' }}</button>
              @if (c.id !== auth.session()?.company?.id) { <button class="btn ghost sm" (click)="visibility(c, !!c.isHidden, !c.isActive)">{{ c.isActive ? 'Deactivate' : 'Activate' }}</button> }
            </td></tr>
        }</tbody>
      </table>
      <div class="cols" style="margin-top:16px">
        <div class="card"><header><h2>Backup current company</h2></header><section class="stack">
          <p class="muted">Downloads a logical backup (zip of JSON tables) of {{ auth.session()?.company?.name }}. No database server rights are needed.</p>
          <div><button class="btn primary" (click)="backup()" [disabled]="!auth.can('Company', 'Export')">Download backup</button></div>
        </section></div>
        <div class="card"><header><h2>Restore into a new company</h2></header><section class="stack">
          <p class="muted">Restores a backup as a separate company (ids are remapped); existing companies are never overwritten.
            Its license carries over only when restored under the same company name; otherwise enter a key for it in Settings › License.</p>
          <input type="file" accept=".zip" (change)="file = $any($event.target).files?.[0] ?? null" />
          <label class="field"><span>New company name</span><input [(ngModel)]="restoreName" /></label>
          <div><button class="btn" (click)="restore()" [disabled]="!file || !restoreName">Restore</button></div>
        </section></div>
      </div>
    </div>
    @if (form(); as f) {
      <app-modal title="New company" (closed)="form.set(null)">
        <div class="form-grid cols-2">
          <label class="field req span-2"><span>Name</span><input [(ngModel)]="f.name" /></label>
          <label class="field req"><span>State</span><select [(ngModel)]="f.stateCode">@for (s of states; track s[0]) {<option [value]="s[0]">{{ s[0] }} {{ s[1] }}</option>}</select></label>
          <label class="field"><span>GSTIN</span><input [(ngModel)]="f.gstin" maxlength="20" style="text-transform:uppercase" />
            @if (f.gstin | gstinError: f.stateCode; as err) { <small class="danger-text">{{ err }}</small> }</label>
          <label class="field span-2"><span>Address</span><input [(ngModel)]="f.address1" /></label>
          <label class="field"><span>City</span><input [(ngModel)]="f.city" /></label>
          <label class="field"><span>Financial year starts</span><input type="date" [(ngModel)]="f.financialYearStart" /></label>
          <label class="field req span-2"><span>License key</span>
            <textarea rows="3" [(ngModel)]="f.licenseKey" spellcheck="false" class="mono" placeholder="NAS1-XXXXX-XXXXX-…"></textarea>
            <small class="muted">Each company needs its own key, issued for this exact company name (and GSTIN, if given). Contact NAS POS support to get one.</small></label>
        </div>
        <button footer class="btn primary" (click)="create(f)" [disabled]="!f.licenseKey?.trim()">Create</button>
      </app-modal>
    }`,
})
export class CompaniesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private router = inject(Router);
  states = STATE_CODES;
  list = signal<CompanyDto[]>([]);
  form = signal<CompanyDto | null>(null);
  file: File | null = null;
  restoreName = '';
  blank = (): CompanyDto => ({ id: 0, name: '', stateCode: this.auth.session()?.company.stateCode ?? '32', financialYearStart: null });
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> { this.list.set(await this.api.get<CompanyDto[]>('/company/mine')); }
  async create(f: CompanyDto): Promise<void> {
    try { await this.api.post('/company', { ...f, gstin: f.gstin?.trim().toUpperCase() || null, financialYearStart: f.financialYearStart || null }); this.form.set(null); this.ui.ok('Company created.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async open(c: CompanyDto): Promise<void> { try { await this.auth.switchTo(c.id); this.router.navigate(['/dashboard']); } catch (e) { this.ui.error(errorMessage(e)); } }
  async visibility(c: CompanyDto, hidden: boolean, active: boolean): Promise<void> {
    try { await this.api.put(`/company/${c.id}/visibility`, {}, { hidden, active }); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async backup(): Promise<void> { try { await this.ui.run(() => this.api.download('/company/backup', undefined, undefined, 'company-backup.zip')); } catch (e) { this.ui.error(errorMessage(e)); } }
  async restore(): Promise<void> {
    const form = new FormData(); form.append('file', this.file!); form.append('name', this.restoreName);
    try { await this.ui.run(() => this.api.upload('/company/restore', form)); this.ui.ok('Restored as a new company.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Year end ================= */

interface Preview { currentYear: string; nextStart: string; nextEnd: string; nextYearExists: boolean; trialBalanceDifference: number; ledgersToCarry: number; netProfit: number;
  batchesToCarry: number; closingStockValue: number; negativeBatches: number; loyaltyCustomers: number; blockers: string[]; }

@Component({
  selector: 'app-year-end',
  standalone: true,
  imports: [MoneyPipe, DmyPipe],
  template: `
    <div class="page" style="max-width:800px">
      <div class="page-header"><h1>Year-end closing</h1></div>
      @if (p(); as p) {
        <div class="card"><section class="stack">
          <div>Closing <b>FY {{ p.currentYear }}</b> opens <b>{{ p.nextStart | dmy }} – {{ p.nextEnd | dmy }}</b>{{ p.nextYearExists ? ' (already created)' : '' }}.</div>
          <table class="grid">
            <tbody>
              <tr><td>Trial balance difference</td><td class="num">{{ p.trialBalanceDifference | money }}</td></tr>
              <tr><td>Ledger balances to carry forward</td><td class="num">{{ p.ledgersToCarry }}</td></tr>
              <tr><td>Net profit (into Profit &amp; Loss A/c)</td><td class="num">{{ p.netProfit | money }}</td></tr>
              <tr><td>Batches carried as opening stock</td><td class="num">{{ p.batchesToCarry }} ({{ p.negativeBatches }} negative)</td></tr>
              <tr><td>Closing stock value</td><td class="num">{{ p.closingStockValue | money }}</td></tr>
              <tr><td>Customers with loyalty points</td><td class="num">{{ p.loyaltyCustomers }}</td></tr>
            </tbody>
          </table>
          @if (p.blockers.length) { <div class="error-box">@for (b of p.blockers; track b) {<div>{{ b }}</div>}</div> }
          <div class="info-box">Runs as one database transaction. Nothing is deleted: the closed year stays fully viewable, but becomes read-only.</div>
          <div class="toolbar">
            <button class="btn danger" (click)="close()" [disabled]="p.blockers.length > 0 || !auth.can('YearEnd', 'Edit')">Close FY {{ p.currentYear }}</button>
            @if (!p.nextYearExists) { <button class="btn" (click)="createNext()">Only create next year (keep this one open)</button> }
          </div>
        </section></div>
      }
    </div>`,
})
export class YearEndComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  p = signal<Preview | null>(null);
  async ngOnInit(): Promise<void> { await this.load(); }
  async load(): Promise<void> { try { this.p.set(await this.api.get<Preview>('/year-end/preview')); } catch (e) { this.ui.error(errorMessage(e)); } }
  async close(): Promise<void> {
    if (!(await this.ui.confirm('Close financial year', `Close FY ${this.p()!.currentYear}? It becomes read-only.`, { danger: true, okText: 'Close year' }))) return;
    try {
      const r = await this.ui.run(() => this.api.post<{ newFinancialYearId: number; newYearName: string }>('/year-end/close'));
      this.ui.ok(`Year closed. Switched to FY ${r.newYearName}.`);
      await this.auth.switchTo(this.auth.session()!.company.id, r.newFinancialYearId);
      await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async createNext(): Promise<void> {
    try { await this.api.post('/company/years/next'); await this.auth.loadSession(); this.ui.ok('Next financial year created.'); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}
