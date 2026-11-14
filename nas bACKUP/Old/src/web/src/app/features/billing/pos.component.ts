import { Component, HostListener, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { applyTax, calculate, emptyDocument, lineFromProduct, r2 } from '../../core/calc';
import { MoneyPipe, QtyPipe } from '../../core/format';
import { Live } from '../../core/live';
import { LookupItem, PartyLookup, ProductLookup, RoundOffMode, TradeDocument, TradeLine } from '../../core/models';
import { Ui } from '../../core/ui';
import { LookupComponent, ModalComponent } from '../../shared/components';
import { BillingRefs, InvoiceOptions, lastSalesperson, partyLabel, partySub, productLabel, productSub, rememberSalesperson } from './billing-shared';

interface Tile { productId: number; code: string; name: string; regionalName?: string; categoryId?: number; price: number; hasImage: boolean; stock: number; }

@Component({
  selector: 'app-pos',
  standalone: true,
  imports: [FormsModule, LookupComponent, ModalComponent, MoneyPipe, QtyPipe],
  template: `
  <div class="pos">
    <div class="pos-left">
      <div class="toolbar" style="padding:8px;background:var(--surface);border-bottom:1px solid var(--line)">
        <div class="grow"><app-lookup #scanBox [search]="searchProduct" [display]="productLabel" [sub]="productSub" [clearOnPick]="true"
             placeholder="Scan barcode / weighing-scale label or search item" (picked)="add($event)" (entered)="scan($event)" /></div>
        @if (auth.flag('EnablePoleDisplay')) { <button class="btn" (click)="connectPole()">{{ pole ? 'Pole ✓' : 'Pole display' }}</button> }
      </div>
      <div class="pos-cats">
        <button [class.active]="cat() === null" (click)="selectCat(null)">All</button>
        @for (c of cats(); track c.id) { <button [class.active]="cat() === c.id" (click)="selectCat(c.id)" [style.borderColor]="c.color || null">{{ c.name }}</button> }
      </div>
      <div class="pos-tiles">
        @for (t of tiles(); track t.productId) {
          <button class="tile" (click)="tile(t)">
            @if (showImages && t.hasImage) { <img [src]="'/api/products/' + t.productId + '/image'" alt="" loading="lazy" /> }
            <span class="t-name">{{ t.name }}</span>
            @if (t.regionalName) { <span class="small muted">{{ t.regionalName }}</span> }
            <span class="t-price">₹ {{ t.price | money }}</span>
            <span class="t-stock">Stock {{ t.stock | qty }}</span>
          </button>
        } @empty { <div class="empty" style="grid-column:1/-1">No items in this category.</div> }
      </div>
    </div>

    <div class="pos-right">
      <div style="padding:8px 12px;border-bottom:1px solid var(--line)" class="stack">
        <div class="row center-y">
          <div class="grow"><app-lookup [search]="searchParty" [display]="partyLabel" [sub]="partySub" [value]="doc().partyName" placeholder="Customer (walk-in)" (picked)="pickParty($event)" /></div>
          <select [(ngModel)]="doc().salespersonId" style="width:130px" aria-label="Salesperson">
            <option [ngValue]="null">Salesperson</option>
            @for (e of refs.employees; track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
          </select>
        </div>
        @if (party(); as p) {
          <div class="toolbar small"><span class="badge">Bal ₹ {{ p.balance | money }}</span>
            @if (p.loyaltySchemeId) { <span class="badge ok">Points {{ p.loyaltyPoints | qty }}</span>
              @if (p.loyaltyPoints >= p.loyaltyTarget && p.loyaltyPoints > 0) { <button class="btn sm" (click)="redeem()">Redeem</button> } }
            <button class="link-btn" (click)="clearParty()">clear</button></div>
        }
      </div>
      <div class="pos-cart">
        @for (l of doc().lines; track $index; let i = $index) {
          <div class="ci" [class.sel]="sel() === i" (click)="sel.set(i)">
            <div><b>{{ l.productName }}</b>@if (l.discountPercent) { <span class="badge warn">-{{ l.discountPercent }}%</span> }</div>
            <div class="num"><b>{{ l.lineTotal | money }}</b></div>
            <div class="q">
              <button class="btn sm" (click)="bump(i, -1); $event.stopPropagation()">−</button>
              <span>{{ l.qty | qty }} × {{ l.rate | money }}</span>
              <button class="btn sm" (click)="bump(i, 1); $event.stopPropagation()">+</button>
            </div>
            <div class="num"><button class="btn ghost sm" (click)="remove(i); $event.stopPropagation()" aria-label="Remove">✕</button></div>
          </div>
        } @empty { <div class="empty">Scan or tap items to start a bill.</div> }
      </div>
      <div class="pos-total">
        <div class="t-row small muted row"><span class="grow">{{ doc().lines.length }} items · qty {{ totalQty() | qty }}</span>
          <span>Tax ₹ {{ tax() | money }}</span>@if (saved() > 0) {<span class="ok-text">· saved ₹ {{ saved() | money }}</span>}</div>
        <div class="big">₹ {{ doc().billAmount | money }}</div>
      </div>
      <div class="keypad">
        @for (k of ['7','8','9','Qty','4','5','6','Rate','1','2','3','Disc%','0','.','⌫','C']; track k) {
          <button [class.op]="k.length > 1 || k === 'C'" (click)="key(k)">{{ k }}</button>
        }
      </div>
      <div class="small muted" style="padding:0 12px">Entry: <b>{{ buffer || '—' }}</b> (select an item, type a number, then Qty / Rate / Disc%)</div>
      <div class="pay-grid">
        <button class="btn primary" (click)="pay('Cash')" [disabled]="busy()">Cash <kbd>F2</kbd></button>
        <button class="btn" (click)="pay('Card')" [disabled]="busy()">Card / UPI</button>
        <button class="btn" (click)="pay('Credit')" [disabled]="busy()">Credit</button>
        <button class="btn" (click)="pay('Mixed')" [disabled]="busy()">Mixed</button>
        <button class="btn" (click)="clear()" [disabled]="busy()">Clear</button>
        <button class="btn" (click)="reprint()" [disabled]="!lastId">Reprint</button>
      </div>
    </div>
  </div>

  @if (desk()) {
    <app-modal title="Cash desk" size="sm" (closed)="desk.set(false)">
      <div class="stack">
        <div class="t-row"><b>Bill</b><b>₹ {{ doc().billAmount | money }}</b></div>
        <label class="field"><span>Tendered</span><input type="number" class="num" [(ngModel)]="tendered" autofocus (keydown.enter)="finishCash()" /></label>
        <div class="denoms">@for (d of [10, 20, 50, 100, 200, 500, 2000]; track d) { <button (click)="tendered = (tendered || 0) + d">+{{ d }}</button> }
          <button (click)="tendered = doc().billAmount">Exact</button></div>
        <div class="grand" style="font-size:28px;text-align:right">Change ₹ {{ (tendered || 0) - doc().billAmount | money }}</div>
      </div>
      <button footer class="btn primary lg" (click)="finishCash()">Complete sale</button>
    </app-modal>
  }

  @if (mixedOpen()) {
    <app-modal title="Payment" size="sm" (closed)="mixedOpen.set(false)">
      <div class="stack">
        <div class="t-row"><b>Bill</b><b>₹ {{ doc().billAmount | money }}</b></div>
        <label class="field"><span>Card / UPI amount</span><input type="number" class="num" [(ngModel)]="doc().cardAmount" (ngModelChange)="rebalance()" /></label>
        <label class="field"><span>Card / bank ledger</span>
          <select [(ngModel)]="doc().cardLedgerId"><option [ngValue]="null">Bank (default)</option>@for (b of refs.bankLedgers; track b.id) {<option [ngValue]="b.id">{{ b.name }}</option>}</select></label>
        <div class="form-grid cols-2">
          <label class="field"><span>Card last 4</span><input [(ngModel)]="doc().cardLast4" maxlength="4" inputmode="numeric" /></label>
          <label class="field"><span>Reference</span><input [(ngModel)]="doc().paymentReference" /></label>
        </div>
        <label class="field"><span>Cash amount</span><input type="number" class="num" [(ngModel)]="doc().cashAmount" /></label>
        <label class="field"><span>Credit (customer account)</span><input type="number" class="num" [(ngModel)]="doc().creditAmount" [disabled]="!party() || party()!.isWalkIn" /></label>
        <div class="muted small">Only the last 4 digits are recorded; card numbers and CVV are never stored.</div>
      </div>
      <button footer class="btn primary lg" (click)="finishMixed()">Complete sale</button>
    </app-modal>
  }
  `,
})
export class PosComponent implements OnInit, OnDestroy {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private live = inject(Live);
  refs = new BillingRefs(this.api);
  productLabel = productLabel;
  productSub = productSub;
  partyLabel = partyLabel;
  partySub = partySub;
  @ViewChild('scanBox') scanBox?: LookupComponent<ProductLookup>;

  doc = signal<TradeDocument>(emptyDocument('POSSales'));
  cats = signal<LookupItem[]>([]);
  cat = signal<number | null>(null);
  tiles = signal<Tile[]>([]);
  party = signal<PartyLookup | null>(null);
  sel = signal(-1);
  busy = signal(false);
  desk = signal(false);
  mixedOpen = signal(false);
  tick = signal(0);
  opts: InvoiceOptions | null = null;
  buffer = '';
  tendered: number | null = null;
  lastId: number | null = null;
  pole: any = null;
  showImages = this.auth.flag('ShowPosImages');
  private sub?: Subscription;

  totalQty = computed(() => { this.tick(); return this.doc().lines.reduce((s, l) => s + (+l.qty || 0), 0); });
  tax = computed(() => { this.tick(); const d = this.doc(); return r2(d.cgstAmount + d.sgstAmount + d.igstAmount + d.cessAmount + d.addCessAmount + d.kfCessAmount); });
  saved = computed(() => { this.tick(); const d = this.doc(); return d.totalMrp > d.billAmount ? r2(d.totalMrp - d.billAmount) : 0; });

  searchProduct = (term: string) => this.api.get<ProductLookup[]>('/products/lookup', { term, priceListId: this.doc().priceListId ?? undefined, take: 20 });
  searchParty = (term: string) => this.api.get<PartyLookup[]>('/ledgers/parties', { type: 'Customer', search: term, take: 15 });

  async ngOnInit(): Promise<void> {
    try {
      await this.refs.load();
      this.opts = await this.api.get<InvoiceOptions>('/company/invoice-options/POSSales');
      const cats = await this.api.get<LookupItem[]>('/lookups/category', { activeOnly: true });
      this.cats.set(cats.filter(c => c.showInPos !== false));
      await this.loadTiles();
      this.reset();
    } catch (e) { this.ui.error(errorMessage(e)); }
    this.sub = this.live.stockChanged.subscribe(() => this.loadTiles());
  }

  ngOnDestroy(): void { this.sub?.unsubscribe(); try { this.pole?.close?.(); } catch { /* ignore */ } }

  private reset(): void {
    const d = emptyDocument('POSSales', (this.auth.setting('DefaultRoundOff') || 'Auto') as RoundOffMode);
    d.taxType = 'IntraState';
    d.salespersonId = this.doc().salespersonId ?? lastSalesperson();
    this.doc.set(d);
    this.party.set(null);
    this.sel.set(-1);
    this.buffer = '';
    this.tick.update(t => t + 1);
    setTimeout(() => this.scanBox?.focus(), 50);
  }

  async selectCat(id: number | null): Promise<void> { this.cat.set(id); await this.loadTiles(); }

  private async loadTiles(): Promise<void> {
    try { this.tiles.set(await this.api.get<Tile[]>('/products/pos-tiles', { categoryId: this.cat() ?? undefined, priceListId: this.doc().priceListId ?? undefined })); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  async tile(t: Tile): Promise<void> {
    const rows = await this.api.get<ProductLookup[]>('/products/lookup', { term: t.code, exact: true, priceListId: this.doc().priceListId ?? undefined });
    const p = rows.find(r => r.stock > 0) ?? rows[0];
    if (p) this.add(p);
  }

  async scan(term: string): Promise<void> {
    try {
      const rows = await this.api.get<ProductLookup[]>('/products/lookup', { term, exact: true, priceListId: this.doc().priceListId ?? undefined });
      if (!rows.length) { this.ui.error(`No item for "${term}".`); return; }
      this.add(rows.find(r => r.stock > 0) ?? rows[0]);
      if (this.scanBox) this.scanBox.text = '';
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  add(p: ProductLookup): void {
    const d = this.doc();
    const existing = d.lines.findIndex(l => l.productId === p.productId && (l.batchId ?? null) === (p.batchId ?? null) && !p.scaleQty);
    if (existing >= 0) {
      d.lines[existing].qty += 1;
      this.sel.set(existing);
    } else {
      const l = lineFromProduct(p, 'POSSales', { inclusive: this.opts?.inclusiveUnitPrice ? true : undefined, kfCess: this.auth.flag('EnableKfCess'), purchase: false });
      if (this.party()?.discountPercent && !l.discountPercent) l.discountPercent = this.party()!.discountPercent;
      d.lines.push(l);
      this.sel.set(d.lines.length - 1);
    }
    this.recalc();
    const line = d.lines[this.sel()];
    this.poleShow(line.productName ?? '', `${line.qty} x ${line.rate.toFixed(2)}`);
    this.scanBox?.focus();
  }

  bump(i: number, delta: number): void {
    const l = this.doc().lines[i];
    l.qty = Math.max(0, (+l.qty || 0) + delta);
    if (l.qty === 0) { this.remove(i); return; }
    this.recalc();
  }

  async remove(i: number): Promise<void> {
    const l = this.doc().lines[i];
    this.doc().lines.splice(i, 1);
    this.sel.set(Math.min(this.sel(), this.doc().lines.length - 1));
    this.recalc();
    try {
      await this.api.post('/documents/void-items', { screen: 'POS', productId: l.productId, productName: l.productName, batch: l.batchCode, qty: l.qty, rate: l.rate, mrp: l.mrp });
    } catch { /* non-blocking */ }
  }

  key(k: string): void {
    const i = this.sel();
    const l: TradeLine | undefined = this.doc().lines[i];
    if (/^[0-9.]$/.test(k)) { this.buffer += k; return; }
    if (k === '⌫') { this.buffer = this.buffer.slice(0, -1); return; }
    if (k === 'C') { this.buffer = ''; return; }
    const v = parseFloat(this.buffer);
    this.buffer = '';
    if (!l || Number.isNaN(v)) return;
    if (k === 'Qty') l.qty = v;
    else if (k === 'Rate') {
      if (this.opts?.disableRateEdit) { this.ui.error('Price editing is disabled for POS.'); return; }
      l.rate = v; applyTax(l, 'POSSales');
    } else if (k === 'Disc%') l.discountPercent = Math.min(100, v);
    this.recalc();
  }

  pickParty(p: PartyLookup): void {
    const d = this.doc();
    this.party.set(p);
    d.partyLedgerId = p.isWalkIn ? null : p.id;
    d.partyName = p.name;
    d.partyPhone = p.mobile;
    d.partyGstin = p.gstin;
    d.partyStateCode = p.stateCode;
    if (p.priceListId) d.priceListId = p.priceListId;
    if (p.salespersonId) d.salespersonId = p.salespersonId;
    this.recalc();
  }

  clearParty(): void {
    const d = this.doc();
    this.party.set(null);
    d.partyLedgerId = null; d.partyName = ''; d.partyPhone = ''; d.partyGstin = ''; d.pointsRedeemed = 0; d.redeemAmount = 0;
    this.recalc();
  }

  async redeem(): Promise<void> {
    const p = this.party()!;
    const v = await this.ui.prompt('Redeem loyalty points', `Points to redeem (available ${p.loyaltyPoints})`, { type: 'number', value: String(p.loyaltyPoints) });
    if (!v) return;
    const pts = Math.min(+v, p.loyaltyPoints);
    this.doc().pointsRedeemed = pts;
    this.doc().redeemAmount = r2(pts);
    this.recalc();
  }

  recalc(): void {
    const d = this.doc();
    d.lines.forEach(l => applyTax(l, 'POSSales'));
    calculate(d, { serviceTaxRate: +this.auth.setting('ServiceTaxRate') || 0, serviceCessRate: +this.auth.setting('ServiceCessRate') || 0, taxClaimable: true });
    this.tick.update(t => t + 1);
    this.poleTotal();
  }

  rebalance(): void {
    const d = this.doc();
    d.cashAmount = Math.max(0, r2(d.billAmount - (+d.cardAmount || 0) - (+d.creditAmount || 0)));
  }

  async pay(mode: 'Cash' | 'Card' | 'Credit' | 'Mixed'): Promise<void> {
    const d = this.doc();
    if (!d.lines.length) { this.ui.error('Add items first.'); return; }
    if (this.auth.flag('RequireSalesperson') && !d.salespersonId) { this.ui.error('Select a salesperson.'); return; }
    if (mode === 'Cash') {
      d.paymentMode = 'Cash';
      if (this.opts?.cashDeskOnSave !== false) { this.tendered = null; this.desk.set(true); return; }
      await this.submit();
    } else if (mode === 'Credit') {
      if (!d.partyLedgerId) { this.ui.error('Select a customer for a credit bill.'); return; }
      d.paymentMode = 'Credit';
      await this.submit();
    } else {
      d.paymentMode = 'Mixed';
      d.cardAmount = mode === 'Card' ? d.billAmount : d.cardAmount || 0;
      d.creditAmount = 0;
      this.rebalance();
      this.mixedOpen.set(true);
    }
  }

  async finishCash(): Promise<void> {
    this.doc().tenderedAmount = this.tendered || 0;
    this.desk.set(false);
    await this.submit();
  }

  async finishMixed(): Promise<void> {
    const d = this.doc();
    const diff = r2(d.billAmount - (+d.cardAmount || 0) - (+d.cashAmount || 0) - (+d.creditAmount || 0));
    if (diff !== 0) { this.ui.error(`Split differs from bill by ₹${diff.toFixed(2)}.`); return; }
    this.mixedOpen.set(false);
    await this.submit();
  }

  private async submit(): Promise<void> {
    this.busy.set(true);
    try {
      const d = this.doc();
      const saved = await this.api.post<TradeDocument>('/documents', { ...d, lines: d.lines.map(l => ({ ...l, batchId: l.batchId || null })) });
      rememberSalesperson(saved.salespersonId);
      this.lastId = saved.id;
      this.ui.ok(`Bill ${saved.documentNo} · ₹ ${saved.billAmount.toFixed(2)}${saved.tenderedAmount ? ` · change ₹ ${(saved.tenderedAmount - saved.cashAmount).toFixed(2)}` : ''}`);
      this.poleShow('THANK YOU', `TOTAL ${saved.billAmount.toFixed(2)}`);
      if (this.opts?.printOnSave !== false && this.auth.can('POS', 'Print')) {
        const go = !this.opts?.confirmPrint || await this.ui.confirm('Print', `Print bill ${saved.documentNo}?`, { okText: 'Print' });
        if (go) await this.api.printPdf(`/documents/${saved.id}/pdf${this.opts?.printSchemeId ? '?scheme=' + this.opts.printSchemeId : ''}`);
      }
      this.reset();
    } catch (e) {
      this.ui.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async reprint(): Promise<void> { if (this.lastId) await this.api.printPdf(`/documents/${this.lastId}/pdf`); }

  async clear(): Promise<void> {
    if (this.doc().lines.length && !(await this.ui.confirm('Clear bill', 'Remove all items from this bill?', { danger: true, okText: 'Clear' }))) return;
    for (const l of [...this.doc().lines]) {
      try { await this.api.post('/documents/void-items', { screen: 'POS', productId: l.productId, productName: l.productName, batch: l.batchCode, qty: l.qty, rate: l.rate, mrp: l.mrp }); } catch { /* ignore */ }
    }
    this.reset();
  }

  /** Customer pole display over the Web Serial API (Chrome/Edge). */
  async connectPole(): Promise<void> {
    const serial = (navigator as any).serial;
    if (!serial) { this.ui.error('This browser does not support serial devices (use Chrome or Edge).'); return; }
    try {
      const port = await serial.requestPort();
      await port.open({ baudRate: +this.auth.setting('PoleDisplayBaud') || 9600 });
      this.pole = port;
      this.poleShow(this.auth.setting('PoleDisplayWelcome') || 'WELCOME', '');
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  private async poleShow(line1: string, line2: string): Promise<void> {
    if (!this.pole?.writable) return;
    const pad = (s: string) => (s ?? '').substring(0, 20).padEnd(20, ' ');
    const writer = this.pole.writable.getWriter();
    try { await writer.write(new TextEncoder().encode('\x0C' + pad(line1) + pad(line2))); } catch { /* ignore */ } finally { writer.releaseLock(); }
  }

  private poleTotal(): void { if (this.pole) this.poleShow('TOTAL', `Rs ${this.doc().billAmount.toFixed(2)}`); }

  @HostListener('window:keydown', ['$event'])
  keys(e: KeyboardEvent): void {
    if (e.key === 'F2') { e.preventDefault(); this.pay('Cash'); }
    else if (e.key === 'F6') { e.preventDefault(); this.pay('Mixed'); }
    else if (e.key === 'Delete' && this.sel() >= 0 && (e.target as HTMLElement).tagName !== 'INPUT') { e.preventDefault(); this.remove(this.sel()); }
  }
}
