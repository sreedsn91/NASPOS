import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { GstinErrorPipe, gstinError, normalizeGstin } from '../../core/gstin';
import { r2 } from '../../core/calc';
import { DmyPipe, MoneyPipe, QtyPipe, toLocalInput, fromLocalInput } from '../../core/format';
import { Employee, IdName, LookupItem, Paged, ProductLookup } from '../../core/models';
import { Ui } from '../../core/ui';
import { AuthImageDirective, ImageCache } from '../../shared/auth-image';
import { LookupComponent, ModalComponent, PagerComponent } from '../../shared/components';

const STATE_CODES: [string, string][] = [
  ['01', 'Jammu & Kashmir'], ['02', 'Himachal Pradesh'], ['03', 'Punjab'], ['04', 'Chandigarh'], ['05', 'Uttarakhand'], ['06', 'Haryana'],
  ['07', 'Delhi'], ['08', 'Rajasthan'], ['09', 'Uttar Pradesh'], ['10', 'Bihar'], ['11', 'Sikkim'], ['12', 'Arunachal Pradesh'], ['13', 'Nagaland'],
  ['14', 'Manipur'], ['15', 'Mizoram'], ['16', 'Tripura'], ['17', 'Meghalaya'], ['18', 'Assam'], ['19', 'West Bengal'], ['20', 'Jharkhand'],
  ['21', 'Odisha'], ['22', 'Chhattisgarh'], ['23', 'Madhya Pradesh'], ['24', 'Gujarat'], ['26', 'Dadra & Nagar Haveli and Daman & Diu'],
  ['27', 'Maharashtra'], ['29', 'Karnataka'], ['30', 'Goa'], ['31', 'Lakshadweep'], ['32', 'Kerala'], ['33', 'Tamil Nadu'], ['34', 'Puducherry'],
  ['35', 'Andaman & Nicobar'], ['36', 'Telangana'], ['37', 'Andhra Pradesh'], ['38', 'Ladakh'], ['97', 'Other Territory'],
];
export { STATE_CODES };

/** GS1 mod-10 check digit for the digits before it (weights 3,1 from the right). */
function eanCheckDigit(body: string): number {
  let sum = 0;
  for (let i = 0; i < body.length; i++) sum += +body[body.length - 1 - i] * (i % 2 === 0 ? 3 : 1);
  return (10 - (sum % 10)) % 10;
}

/** Why a barcode can't be linked (same rules as the server), or null. EAN-8/UPC-A/EAN-13/GTIN-14 check digits are verified. */
export function barcodeError(code: string | null | undefined): string | null {
  const c = (code ?? '').trim();
  if (!c) return null;
  if (/[^\x21-\x7e]/.test(c)) return 'Only letters, digits and symbols — no spaces.';
  if (/^\d+$/.test(c) && [8, 12, 13, 14].includes(c.length)) {
    const want = eanCheckDigit(c.slice(0, -1));
    if (+c[c.length - 1] !== want) return `Check digit is wrong (last digit should be ${want}) — scan again or check for a typo.`;
  }
  return null;
}

/* ================= Products ================= */

interface ProductRow { id: number; code: string; name: string; categoryName?: string; hsnCode?: string; gstRate: number; purchaseRate: number; saleRate: number; mrp: number; mainUnit: string; stock: number; reorderLevel: number; isActive: boolean; itemClass: string; barcode?: string | null; }

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe, QtyPipe, PagerComponent],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Products</h1>
        @if (auth.can('Printing', 'Print')) {
          <button class="btn" (click)="printLabels()" [disabled]="!pickedIds().length" title="Tick items in the list first">Print labels{{ pickedIds().length ? ' (' + pickedIds().length + ')' : '' }}</button> }
        @if (auth.can('Product', 'Edit')) { <button class="btn" (click)="generateMissing()" title="Give every item without a barcode a unique in-store barcode">Generate missing barcodes</button> }
        @if (auth.can('Product', 'Add')) { <a class="btn primary" routerLink="/masters/products/new">New item</a> }
        <a class="btn" routerLink="/tools/import-export">Import / Export</a></div>
      <div class="card"><section class="form-grid cols-6">
        <label class="field span-2"><span>Search (name, code, barcode, HSN)</span><input [(ngModel)]="search" (keydown.enter)="load(1)" autofocus /></label>
        <label class="field"><span>Category</span><select [(ngModel)]="categoryId" (ngModelChange)="load(1)"><option [ngValue]="null">All</option>
          @for (c of categories(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }</select></label>
        <label class="field"><span>Status</span><select [(ngModel)]="active" (ngModelChange)="load(1)"><option [ngValue]="null">All</option><option [ngValue]="true">Active</option><option [ngValue]="false">Inactive</option></select></label>
        <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="belowRol" (ngModelChange)="load(1)" /> At/below reorder level</label>
      </section></div>
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th style="width:36px"><input type="checkbox" [checked]="allPicked()" (change)="pickAll($any($event.target).checked)" aria-label="Select all on this page" /></th>
            <th>Code</th><th>Name</th><th>Barcode</th><th>Category</th><th>HSN</th><th class="num">GST%</th><th class="num">Purchase</th><th class="num">Sale</th><th class="num">MRP</th><th class="num">Stock</th><th>Unit</th></tr></thead>
          <tbody>
            @for (p of rows(); track p.id) {
              <tr class="clickable" [class.cancelled]="!p.isActive" [class.danger]="p.reorderLevel > 0 && p.stock <= p.reorderLevel" (click)="router.navigate(['/masters/products', p.id])">
                <td (click)="$event.stopPropagation()"><input type="checkbox" [checked]="picked().has(p.id)" (change)="pick(p.id, $any($event.target).checked)" [attr.aria-label]="'Select ' + p.name" /></td>
                <td>{{ p.code }}</td><td><b>{{ p.name }}</b> @if (p.itemClass === 'Service') {<span class="badge">service</span>}</td>
                <td class="mono small">@if (p.barcode) { {{ p.barcode }} } @else if (p.itemClass !== 'Service') { <span class="muted">—</span> }</td>
                <td>{{ p.categoryName }}</td><td>{{ p.hsnCode }}</td>
                <td class="num">{{ p.gstRate | qty }}</td><td class="num">{{ p.purchaseRate | money }}</td><td class="num">{{ p.saleRate | money }}</td><td class="num">{{ p.mrp | money }}</td>
                <td class="num">{{ p.stock | qty }}</td><td>{{ p.mainUnit }}</td></tr>
            } @empty { <tr><td colspan="12" class="empty">No items.</td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="100" (go)="load($event)" />
    </div>`,
})
export class ProductListComponent implements OnInit {
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<ProductRow[]>([]);
  total = signal(0);
  page = signal(1);
  categories = signal<LookupItem[]>([]);
  search = '';
  categoryId: number | null = null;
  active: boolean | null = true;
  belowRol = false;
  /** Ticked items, kept across pages and searches until printed. */
  picked = signal(new Set<number>());
  pickedIds = computed(() => [...this.picked()]);
  allPicked = computed(() => this.rows().length > 0 && this.rows().every(r => this.picked().has(r.id)));
  async ngOnInit(): Promise<void> {
    this.categories.set(await this.api.get<LookupItem[]>('/lookups/category'));
    await this.load(1);
  }
  pick(id: number, on: boolean): void {
    const s = new Set(this.picked());
    if (on) s.add(id); else s.delete(id);
    this.picked.set(s);
  }
  pickAll(on: boolean): void {
    const s = new Set(this.picked());
    for (const r of this.rows()) { if (on) s.add(r.id); else s.delete(r.id); }
    this.picked.set(s);
  }
  printLabels(): void { this.router.navigate(['/inventory/labels'], { queryParams: { productIds: this.pickedIds().join(',') } }); }
  async generateMissing(): Promise<void> {
    const ids = this.pickedIds();
    const scope = ids.length ? `the ${ids.length} ticked item(s)` : 'every active item';
    if (!(await this.ui.confirm('Generate barcodes', `Create a unique in-store barcode for ${scope} that has no barcode yet? Items that already have one are not changed.`, { okText: 'Generate' }))) return;
    try {
      const r = await this.ui.run(() => this.api.post<{ count: number }>('/products/barcode/generate-missing', { productIds: ids.length ? ids : null }));
      this.ui.ok(r.count ? `${r.count} barcode(s) generated.` : 'Every item already has a barcode.');
      await this.load(this.page());
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async load(p: number): Promise<void> {
    try {
      const r = await this.api.get<Paged<ProductRow>>('/products', { search: this.search, categoryId: this.categoryId, active: this.active, belowReorder: this.belowRol || undefined, page: p, pageSize: 100 });
      this.rows.set(r.items); this.total.set(r.total); this.page.set(p);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

interface QuickLookup { kind: 'category' | 'manufacturer'; name: string; showInPos: boolean; color: string; busy: boolean; error: string; }

interface Batch { id: number; productId: number; code: string; purchaseRate: number; saleRate: number; mrp: number; cost: number; expiryDate?: string | null; isActive: boolean; stock: number; }

@Component({
  selector: 'app-product-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe, QtyPipe, ModalComponent, AuthImageDirective],
  template: `
    <div class="page">
      <div class="page-header">
        <a class="btn ghost" routerLink="/masters/products">‹ Products</a>
        <h1>{{ p.id ? p.name : 'New item' }}</h1>
        @if (p.id && auth.can('Product', 'Delete')) { <button class="btn danger" (click)="remove()">Delete</button> }
        @if (p.id) { <button class="btn" (click)="router.navigate(['/reports/stock-history'], { queryParams: { productId: p.id } })">Stock history</button> }
        @if (p.id && auth.can('Printing', 'Print')) {
          <button class="btn" (click)="printLabels()" [disabled]="(p.defaultBarcode || '') !== savedBarcode" [title]="(p.defaultBarcode || '') !== savedBarcode ? 'Save the item first' : 'Print barcode labels for this item'">Print labels</button> }
        <button class="btn primary" (click)="save()" [disabled]="!canSave()">Save</button>
      </div>
      @if (error()) { <div class="error-box" style="margin-bottom:10px">{{ error() }}</div> }
      <div class="tabs">
        @for (t of tabs; track t) { <button [class.active]="tab() === t" (click)="tab.set(t)">{{ t }}</button> }
      </div>
      @if (tab() === 'General') {
        <div class="card"><section class="form-grid cols-4">
          <label class="field"><span>Code</span><input [(ngModel)]="p.code" placeholder="auto" /></label>
          <label class="field req span-2"><span>Name</span><input [(ngModel)]="p.name" autofocus /></label>
          <label class="field"><span>Regional name</span><input [(ngModel)]="p.regionalName" /></label>
          <label class="field"><span>Item class</span><select [(ngModel)]="p.itemClass"><option value="Goods">Goods</option><option value="Service">Service</option></select></label>
          <label class="field"><span>HSN / SAC</span><input [(ngModel)]="p.hsnCode" /></label>
          <div class="field"><span>Category
              @if (auth.can('Masters', 'Add')) { <button type="button" class="link-btn small" (click)="newLookup('category')">+ new</button> }</span>
            <select [(ngModel)]="p.categoryId" aria-label="Category"><option [ngValue]="null">—</option>@for (c of categories(); track c.id) {<option [ngValue]="c.id">{{ c.name }}</option>}</select></div>
          <div class="field"><span>Manufacturer
              @if (auth.can('Masters', 'Add')) { <button type="button" class="link-btn small" (click)="newLookup('manufacturer')">+ new</button> }</span>
            <select [(ngModel)]="p.manufacturerId" aria-label="Manufacturer"><option [ngValue]="null">—</option>@for (c of manufacturers(); track c.id) {<option [ngValue]="c.id">{{ c.name }}</option>}</select></div>
          <label class="field"><span>Main unit</span><input [(ngModel)]="p.mainUnit" /></label>
          @if (auth.flag('EnableUnitConversion')) {
            <label class="field"><span>Alternate unit</span><input [(ngModel)]="p.altUnit" placeholder="e.g. BOX" /></label>
            <label class="field"><span>Base units per alt. unit</span><input type="number" class="num" [(ngModel)]="p.conversionFactor" /></label>
          }
          <div class="field span-2"><span>Barcode <small class="muted">— scan the one printed on the pack, or generate one</small></span>
            <div class="row">
              <input [(ngModel)]="p.defaultBarcode" (ngModelChange)="barcodeChanged()" (keydown.enter)="$event.preventDefault()"
                     placeholder="Scan or type barcode" maxlength="48" autocomplete="off" class="mono" />
              @if (!p.defaultBarcode) { <button class="btn" type="button" (click)="generateBarcode()" [disabled]="!canSave()" title="Create a unique in-store EAN-13 barcode">Generate</button> }
              @else { <button class="btn ghost" type="button" (click)="p.defaultBarcode = ''; barcodeChanged()" [disabled]="!canSave()" title="Unlink this barcode">✕</button> }
            </div>
            @if (barcodeWarning(); as w) { <small class="danger-text">{{ w }}</small> }
            @else if (barcodeSvg()) { <img [src]="barcodeSvg()" alt="Barcode preview" class="barcode-preview" /> }
            @else if (p.id && !p.defaultBarcode) { <small class="muted">No barcode — labels will print the item code {{ p.code }}.</small> }
          </div>
          <label class="field"><span>Reorder level</span><input type="number" class="num" [(ngModel)]="p.reorderLevel" /></label>
          <label class="field"><span>Rack</span><input [(ngModel)]="p.rack" /></label>
          <label class="field"><span>Discount %</span><input type="number" class="num" [(ngModel)]="p.discountPercent" /></label>
          <label class="field"><span>Salesperson commission %</span><input type="number" class="num" [(ngModel)]="p.commissionPercent" /></label>
          @if (auth.flag('EnableExpiry')) { <label class="check"><input type="checkbox" [(ngModel)]="p.trackExpiry" /> Track expiry</label> }
          @if (auth.flag('EnableWeighingScale')) {
            <label class="check"><input type="checkbox" [(ngModel)]="p.isScaleItem" /> Weighing-scale item</label>
            <label class="field"><span>PLU code</span><input [(ngModel)]="p.pluCode" /></label>
          }
          <label class="field"><span>Pieces per box (tiles)</span><input type="number" class="num" [(ngModel)]="p.piecesPerBox" /></label>
          <label class="field"><span>Sq.ft per box (tiles)</span><input type="number" class="num" [(ngModel)]="p.sqftPerBox" /></label>
          <label class="field span-2"><span>Remarks</span><input [(ngModel)]="p.remarks" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="p.isActive" /> Active</label>
          @if (p.id) { <div class="field"><span>Current stock</span><b>{{ p.stock | qty }} {{ p.mainUnit }}</b></div> }
        </section></div>
      }
      @if (tab() === 'Tax & Rates') {
        <div class="card"><section class="form-grid cols-4">
          <label class="field"><span>GST %</span><select [ngModel]="p.igstRate" (ngModelChange)="setGst($event)">
            @for (g of [0, 0.25, 1, 1.5, 3, 5, 12, 18, 28, 40]; track g) { <option [ngValue]="g">{{ g }}%</option> }</select></label>
          <div class="field"><span>Split</span><span>CGST {{ p.cgstRate }}% + SGST {{ p.sgstRate }}% · IGST {{ p.igstRate }}%</span></div>
          @if (auth.flag('EnableCess')) { <label class="field"><span>Cess %</span><input type="number" class="num" [(ngModel)]="p.cessRate" /></label> }
          @if (auth.flag('EnableAddCess')) { <label class="field"><span>Add. cess per unit ₹</span><input type="number" class="num" [(ngModel)]="p.addCessPerUnit" /></label> }
          @if (auth.flag('EnableKfCess')) { <label class="field"><span>KF cess %</span><input type="number" class="num" [(ngModel)]="p.kfCessRate" /></label> }
          <label class="check"><input type="checkbox" [(ngModel)]="p.saleTaxInclusive" /> Sale rate includes tax</label>
          <label class="check"><input type="checkbox" [(ngModel)]="p.purchaseTaxInclusive" /> Purchase rate includes tax</label>
          <label class="check span-all"><input type="checkbox" [(ngModel)]="p.hasSecondaryTax" /> Second GST slab above a rate threshold (e.g. apparel)</label>
          @if (p.hasSecondaryTax) {
            <label class="field"><span>Threshold rate ₹</span><input type="number" class="num" [(ngModel)]="p.secondaryTaxThreshold" /></label>
            <label class="field"><span>Second GST %</span><input type="number" class="num" [ngModel]="p.igst2Rate" (ngModelChange)="p.igst2Rate = +$event; p.cgst2Rate = p.sgst2Rate = +$event / 2" /></label>
          }
          <hr class="span-all" style="border:0;border-top:1px solid var(--line)" />
          <label class="field"><span>Purchase rate</span><input type="number" class="num" [(ngModel)]="p.purchaseRate" (ngModelChange)="margins()" /></label>
          <label class="field"><span>Sale rate</span><input type="number" class="num" [(ngModel)]="p.saleRate" (ngModelChange)="margins()" /></label>
          <label class="field"><span>MRP</span><input type="number" class="num" [(ngModel)]="p.mrp" (ngModelChange)="margins()" /></label>
          <span></span>
          <label class="field"><span>Sale margin % (sets sale rate)</span><input type="number" class="num" [ngModel]="saleMargin()" (ngModelChange)="applyMargin('sale', $event)" /></label>
          <label class="field"><span>MRP margin % (sets MRP)</span><input type="number" class="num" [ngModel]="mrpMargin()" (ngModelChange)="applyMargin('mrp', $event)" /></label>
        </section></div>
      }
      @if (tab() === 'Batches' && p.id) {
        <div class="table-wrap">
          <table class="grid">
            <thead><tr><th>Batch / barcode</th><th class="num">Purchase</th><th class="num">Cost</th><th class="num">Sale rate</th><th class="num">MRP</th><th>Expiry</th><th class="num">Stock</th><th>Active</th><th></th></tr></thead>
            <tbody>
              @for (b of batches(); track b.id) {
                <tr><td class="mono">{{ b.code }}</td><td class="num">{{ b.purchaseRate | money }}</td><td class="num">{{ b.cost | money }}</td>
                  <td><input type="number" class="num" [(ngModel)]="b.saleRate" style="width:90px" /></td><td><input type="number" class="num" [(ngModel)]="b.mrp" style="width:90px" /></td>
                  <td><input type="date" [(ngModel)]="b.expiryDate" /></td><td class="num">{{ b.stock | qty }}</td><td><input type="checkbox" [(ngModel)]="b.isActive" /></td>
                  <td class="actions"><button class="btn sm" (click)="saveBatch(b)">Save</button></td></tr>
              } @empty { <tr><td colspan="9" class="empty">No batches yet — they are created by purchases and opening stock.</td></tr> }
            </tbody>
          </table>
        </div>
      }
      @if (tab() === 'Image' && p.id) {
        <div class="card"><section class="row">
          <img [appAuthSrc]="imageUrl()" (load)="noImage.set(false)" alt="Item image" style="width:200px;height:200px;object-fit:contain;border:1px solid var(--line);border-radius:8px;background:var(--surface-2)" (error)="noImage.set(true)" [hidden]="noImage()" />
          @if (noImage()) { <div class="empty" style="width:200px">No image</div> }
          <div class="stack"><input type="file" accept="image/png,image/jpeg,image/webp,image/gif" (change)="upload($event)" />
            <button class="btn" (click)="clearImage()">Remove image</button><span class="muted small">PNG, JPEG, WebP or GIF up to 2 MB. Shown on POS tiles.</span></div>
        </section></div>
      }
    </div>
    @if (quick(); as q) {
      <app-modal [title]="q.kind === 'category' ? 'New category' : 'New manufacturer'" size="sm" (closed)="quick.set(null)">
        <div class="stack">
          @if (q.error) { <div class="error-box">{{ q.error }}</div> }
          <label class="field req"><span>Name</span><input [(ngModel)]="q.name" (keydown.enter)="saveLookup(q)" autofocus maxlength="100" /></label>
          @if (q.kind === 'category') {
            <label class="check"><input type="checkbox" [(ngModel)]="q.showInPos" /> Show as a button in Touch POS</label>
            <label class="field"><span>Button colour</span><input type="color" [(ngModel)]="q.color" style="width:60px;padding:0" /></label>
          }
        </div>
        <button footer class="btn" (click)="quick.set(null)">Cancel</button>
        <button footer class="btn primary" (click)="saveLookup(q)" [disabled]="q.busy || !q.name.trim()">Save &amp; select</button>
      </app-modal>
    }`,
})
export class ProductEditorComponent implements OnInit {
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  tabs = ['General', 'Tax & Rates', 'Batches', 'Image'];
  tab = signal('General');
  p: any = this.blank();
  categories = signal<LookupItem[]>([]);
  manufacturers = signal<LookupItem[]>([]);
  batches = signal<Batch[]>([]);
  error = signal('');
  noImage = signal(false);
  imgVer = signal(0);
  t = signal(0);
  private images = inject(ImageCache);
  // t() is bumped whenever another item is loaded, so the URL follows p.id (a plain field).
  imageUrl = computed(() => { this.t(); return this.p.id ? `/api/products/${this.p.id}/image?v=${this.imgVer()}` : null; });
  saleMargin = computed(() => { this.t(); return this.p.purchaseRate ? r2(((this.p.saleRate - this.p.purchaseRate) * 100) / this.p.purchaseRate) : 0; });
  mrpMargin = computed(() => { this.t(); return this.p.purchaseRate && this.p.mrp ? r2(((this.p.mrp - this.p.purchaseRate) * 100) / this.p.purchaseRate) : 0; });
  canSave = computed(() => this.auth.can('Product', this.p.id ? 'Edit' : 'Add'));
  /** Barcode as last saved, so labels are never printed for an unsaved change. */
  savedBarcode = '';
  barcodeWarning = signal<string | null>(null);
  barcodeSvg = signal('');
  private previewTimer?: ReturnType<typeof setTimeout>;

  private blank(): any {
    return { id: 0, code: '', name: '', itemClass: 'Goods', cgstRate: 0, sgstRate: 0, igstRate: 0, gstRate: 0, cessRate: 0, addCessPerUnit: 0, kfCessRate: 0,
      purchaseRate: 0, saleRate: 0, mrp: 0, mainUnit: 'NOS', conversionFactor: 1, reorderLevel: 0, discountPercent: 0, commissionPercent: 0,
      isActive: true, categoryId: null, manufacturerId: null, hasSecondaryTax: false, secondaryTaxThreshold: 0, cgst2Rate: 0, sgst2Rate: 0, igst2Rate: 0,
      piecesPerBox: 0, sqftPerBox: 0 };
  }

  ngOnInit(): void {
    // Subscribe first so a fresh form is never reset after the user has started typing.
    this.route.paramMap.subscribe(async pm => {
      const id = pm.get('id');
      this.error.set('');
      if (id && id !== 'new') await this.load(+id);
      else if (this.p.id) { this.p = this.blank(); this.batches.set([]); this.savedBarcode = ''; }
      this.barcodeChanged();
      this.t.set(this.t() + 1);
    });
    Promise.all([this.api.get<LookupItem[]>('/lookups/category', { activeOnly: true }), this.api.get<LookupItem[]>('/lookups/manufacturer', { activeOnly: true })])
      .then(([c, m]) => { this.categories.set(c); this.manufacturers.set(m); })
      .catch(e => this.ui.error(errorMessage(e)));
  }

  private async load(id: number): Promise<void> {
    try {
      this.p = await this.api.get(`/products/${id}`);
      this.savedBarcode = this.p.defaultBarcode ?? '';
      this.barcodeChanged();
      this.batches.set(await this.api.get<Batch[]>(`/products/${id}/batches`));
      this.noImage.set(false);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  /** Validates as the user scans/types and refreshes the preview once input settles. */
  barcodeChanged(): void {
    const code = (this.p.defaultBarcode ?? '').trim();
    this.barcodeWarning.set(barcodeError(code));
    clearTimeout(this.previewTimer);
    if (!code || this.barcodeWarning()) { this.barcodeSvg.set(''); return; }
    this.previewTimer = setTimeout(async () => {
      try {
        const svg = await this.api.text('/print/barcode.svg', { text: code });
        if ((this.p.defaultBarcode ?? '').trim() === code) this.barcodeSvg.set('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg));
      } catch { this.barcodeSvg.set(''); }
    }, 250);
  }

  async generateBarcode(): Promise<void> {
    try {
      const r = await this.api.post<{ barcode: string }>('/products/barcode/generate');
      this.p.defaultBarcode = r.barcode;
      this.barcodeChanged();
      this.ui.ok(`Barcode ${r.barcode} generated — save the item to link it.`);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  printLabels(): void { this.router.navigate(['/inventory/labels'], { queryParams: { productIds: this.p.id } }); }

  /** Quick-add popup for a category / manufacturer without leaving the item form. */
  quick = signal<QuickLookup | null>(null);

  newLookup(kind: 'category' | 'manufacturer'): void {
    this.quick.set({ kind, name: '', showInPos: true, color: '#1f5fbf', busy: false, error: '' });
  }

  async saveLookup(q: QuickLookup): Promise<void> {
    const name = q.name.trim();
    if (!name || q.busy) return;
    const list = q.kind === 'category' ? this.categories : this.manufacturers;
    const existing = list().find(x => x.name.toLowerCase() === name.toLowerCase());
    if (existing) { this.pickLookup(q.kind, existing.id); this.quick.set(null); this.ui.ok(`"${existing.name}" already exists — selected it.`); return; }
    q.busy = true; q.error = '';
    try {
      const r = await this.api.post<{ id: number }>(`/lookups/${q.kind}`,
        { id: 0, name, isActive: true, showInPos: q.kind === 'category' ? q.showInPos : true, sortOrder: 0, color: q.kind === 'category' ? q.color : null });
      list.set(await this.api.get<LookupItem[]>(`/lookups/${q.kind}`, { activeOnly: true }));
      this.pickLookup(q.kind, r.id);
      this.quick.set(null);
      this.ui.ok(`${q.kind === 'category' ? 'Category' : 'Manufacturer'} "${name}" added.`);
    } catch (e) { q.error = errorMessage(e); q.busy = false; }
  }

  private pickLookup(kind: 'category' | 'manufacturer', id: number): void {
    if (kind === 'category') this.p.categoryId = id; else this.p.manufacturerId = id;
  }

  setGst(g: number): void { this.p.igstRate = g; this.p.cgstRate = g / 2; this.p.sgstRate = g / 2; this.p.gstRate = 0; }
  margins(): void { this.t.set(this.t() + 1); }
  applyMargin(kind: 'sale' | 'mrp', pct: number): void {
    const v = r2(this.p.purchaseRate * (1 + (+pct || 0) / 100));
    if (kind === 'sale') this.p.saleRate = v; else this.p.mrp = v;
    this.margins();
  }

  async save(): Promise<void> {
    this.error.set('');
    if (this.barcodeWarning()) { this.error.set('Barcode: ' + this.barcodeWarning()); this.tab.set('General'); return; }
    try {
      const body = { ...this.p, gstRate: 0, altUnit: this.p.altUnit || null, defaultBarcode: this.p.defaultBarcode || null, pluCode: this.p.pluCode || null };
      const r = this.p.id ? await this.api.put<{ id: number }>(`/products/${this.p.id}`, body) : await this.api.post<{ id: number }>('/products', body);
      this.ui.ok('Item saved.');
      if (!this.p.id) this.router.navigate(['/masters/products', r.id], { replaceUrl: true }); else await this.load(r.id);
    } catch (e) { this.error.set(errorMessage(e)); }
  }

  async remove(): Promise<void> {
    if (!(await this.ui.confirm('Delete item', `Delete ${this.p.name}?`, { danger: true, okText: 'Delete' }))) return;
    try { await this.api.delete(`/products/${this.p.id}`); this.ui.ok('Deleted.'); this.router.navigate(['/masters/products']); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async saveBatch(b: Batch): Promise<void> {
    try { await this.api.put(`/products/batches/${b.id}`, { ...b, expiryDate: b.expiryDate || null }); this.ui.ok('Batch updated.'); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async upload(e: Event): Promise<void> {
    const f = (e.target as HTMLInputElement).files?.[0];
    if (!f) return;
    const form = new FormData();
    form.append('file', f);
    try {
      await this.api.upload(`/products/${this.p.id}/image`, form, undefined, 'PUT');
      this.images.invalidate(`/api/products/${this.p.id}/image`);
      this.noImage.set(false); this.imgVer.update(v => v + 1); this.ui.ok('Image saved.');
    } catch (err) { this.ui.error(errorMessage(err)); }
    (e.target as HTMLInputElement).value = '';
  }
  async clearImage(): Promise<void> {
    try {
      await this.api.upload(`/products/${this.p.id}/image`, new FormData(), undefined, 'PUT');
      this.images.invalidate(`/api/products/${this.p.id}/image`);
      this.noImage.set(true); this.imgVer.update(v => v + 1);
    } catch (err) { this.ui.error(errorMessage(err)); }
  }
}

/* ================= Parties / ledgers ================= */

interface LedgerRow { id: number; code: string; name: string; groupName?: string; partyType: string; mobile?: string; city?: string; gstin?: string; balance: number; isActive: boolean; isSystem: boolean; }
const PARTY_SCREEN: Record<string, string> = { Customer: 'Customer', Supplier: 'Supplier', Employee: 'Employee', None: 'Ledger' };

@Component({
  selector: 'app-party-list',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe, PagerComponent],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>{{ title() }}</h1>
        @if (auth.can(screen(), 'Add')) { <a class="btn primary" [routerLink]="['/masters/ledger', 'new']" [queryParams]="{ type: type() }">New</a> }
        @if (type() === 'Customer' || type() === 'Supplier') { <a class="btn" routerLink="/tools/import-export">Import / Export</a> }</div>
      <div class="card"><section class="form-grid cols-6">
        <label class="field span-2"><span>Search (name, code, mobile, GSTIN)</span><input [(ngModel)]="search" (keydown.enter)="load(1)" /></label>
        <label class="field"><span>Status</span><select [(ngModel)]="active" (ngModelChange)="load(1)"><option [ngValue]="null">All</option><option [ngValue]="true">Active</option><option [ngValue]="false">Inactive</option></select></label>
      </section></div>
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th>Code</th><th>Name</th><th>Group</th><th>Mobile</th><th>City</th><th>GSTIN</th><th class="num">Balance</th><th></th></tr></thead>
          <tbody>
            @for (l of rows(); track l.id) {
              <tr class="clickable" [class.cancelled]="!l.isActive" (click)="router.navigate(['/masters/ledger', l.id])">
                <td>{{ l.code }}</td><td><b>{{ l.name }}</b> @if (l.isSystem) {<span class="badge">system</span>}</td><td>{{ l.groupName }}</td><td>{{ l.mobile }}</td><td>{{ l.city }}</td>
                <td class="mono">{{ l.gstin }}</td><td class="num">{{ abs(l.balance) | money }} {{ l.balance > 0 ? 'Dr' : l.balance < 0 ? 'Cr' : '' }}</td>
                <td class="actions"><button class="btn sm" (click)="statement(l); $event.stopPropagation()">Statement</button></td></tr>
            } @empty { <tr><td colspan="8" class="empty">No records.</td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="100" (go)="load($event)" />
    </div>`,
})
export class PartyListComponent implements OnInit {
  auth = inject(Auth);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  type = signal('Customer');
  screen = computed(() => PARTY_SCREEN[this.type()] ?? 'Ledger');
  title = computed(() => ({ Customer: 'Customers', Supplier: 'Suppliers', Employee: 'Employees', None: 'All ledgers' } as Record<string, string>)[this.type()]);
  rows = signal<LedgerRow[]>([]);
  total = signal(0);
  page = signal(1);
  search = '';
  active: boolean | null = true;
  abs = Math.abs;
  ngOnInit(): void { this.route.paramMap.subscribe(pm => { this.type.set(pm.get('type') ?? 'Customer'); this.load(1); }); }
  async load(p: number): Promise<void> {
    if (!this.auth.can(this.screen())) { this.ui.error('No access.'); return; }
    try {
      const r = await this.api.get<Paged<LedgerRow>>('/ledgers', { partyType: this.type() === 'None' ? undefined : this.type(), search: this.search, active: this.active, page: p, pageSize: 100 });
      this.rows.set(r.items); this.total.set(r.total); this.page.set(p);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  statement(l: LedgerRow): void { this.router.navigate(['/reports/ledger-statement'], { queryParams: { ledgerId: l.id, ledgerName: l.name } }); }
}

@Component({
  selector: 'app-ledger-editor',
  standalone: true,
  imports: [FormsModule, MoneyPipe, QtyPipe, GstinErrorPipe],
  template: `
    <div class="page">
      <div class="page-header">
        <button class="btn ghost" (click)="back()">‹ Back</button>
        <h1>{{ l.id ? l.name : 'New ' + (l.partyType === 'None' ? 'ledger' : l.partyType.toLowerCase()) }}</h1>
        @if (l.id) { <span class="badge">Balance {{ abs(l.balance) | money }} {{ l.balance >= 0 ? 'Dr' : 'Cr' }}</span>
          @if (l.loyaltyPoints) { <span class="badge ok">Points {{ l.loyaltyPoints | qty }}</span> }
          <button class="btn" (click)="statement()">Statement</button> }
        @if (l.id && !l.isSystem) { <button class="btn danger" (click)="remove()">Delete</button> }
        <button class="btn primary" (click)="save()">Save</button>
      </div>
      @if (error()) { <div class="error-box" style="margin-bottom:10px">{{ error() }}</div> }
      <div class="card"><section class="form-grid cols-4">
        <label class="field"><span>Code</span><input [(ngModel)]="l.code" placeholder="auto" /></label>
        <label class="field req span-2"><span>Name</span><input [(ngModel)]="l.name" autofocus /></label>
        <label class="field"><span>Print name</span><input [(ngModel)]="l.printName" /></label>
        @if (l.partyType === 'Customer' || l.partyType === 'Supplier') {
          <label class="field"><span>Type</span><select [(ngModel)]="l.isCorporate">
            <option [ngValue]="false">Individual</option><option [ngValue]="true">Corporate (company / firm)</option></select>
            <small class="muted">{{ l.isCorporate ? 'Same name allowed (branches, sister firms) — tell them apart by code / GSTIN.' : 'Name must be unique.' }}</small></label>
        }
        <label class="field"><span>Account group</span><select [(ngModel)]="l.accountGroupId" [disabled]="l.isSystem">
          @for (g of groups(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }</select></label>
        <label class="field"><span>Regional name</span><input [(ngModel)]="l.regionalName" /></label>
        <label class="field"><span>Mobile</span><input [(ngModel)]="l.mobile" /></label>
        <label class="field"><span>Phone</span><input [(ngModel)]="l.phone" /></label>
        <label class="field"><span>E-mail</span><input type="email" [(ngModel)]="l.email" /></label>
        <label class="field span-2"><span>Address</span><input [(ngModel)]="l.address" /></label>
        <label class="field"><span>City</span><input [(ngModel)]="l.city" /></label>
        <label class="field"><span>GSTIN</span><input [(ngModel)]="l.gstin" (change)="onGstin()" maxlength="20" style="text-transform:uppercase" />
          @if (l.gstin | gstinError: l.stateCode; as err) { <small class="danger-text">{{ err }}</small> }</label>
        <label class="field"><span>State (place of supply)</span><select [(ngModel)]="l.stateCode"><option [ngValue]="null">—</option>
          @for (s of states; track s[0]) { <option [value]="s[0]">{{ s[0] }} {{ s[1] }}</option> }</select></label>
        <label class="field"><span>Area</span><select [(ngModel)]="l.areaId"><option [ngValue]="null">—</option>@for (a of areas(); track a.id) {<option [ngValue]="a.id">{{ a.name }}</option>}</select></label>
        @if (l.partyType === 'Customer' || l.partyType === 'Supplier') {
          <label class="field"><span>Credit limit</span><input type="number" class="num" [(ngModel)]="l.creditLimit" /></label>
          <label class="field"><span>Credit days</span><input type="number" class="num" [(ngModel)]="l.creditDays" /></label>
          <label class="field"><span>Discount %</span><input type="number" class="num" [(ngModel)]="l.discountPercent" /></label>
          <label class="field"><span>Price list</span><select [(ngModel)]="l.priceListId"><option [ngValue]="null">Default</option>@for (p of priceLists(); track p.id) {<option [ngValue]="p.id">{{ p.name }}</option>}</select></label>
          <label class="field"><span>Loyalty scheme</span><select [(ngModel)]="l.loyaltySchemeId"><option [ngValue]="null">None</option>@for (s of schemes(); track s.id) {<option [ngValue]="s.id">{{ s.name }}</option>}</select></label>
          <label class="field"><span>Default salesperson</span><select [(ngModel)]="l.salespersonId"><option [ngValue]="null">—</option>@for (e of employees(); track e.id) {<option [ngValue]="e.id">{{ e.name }}</option>}</select></label>
          <label class="check"><input type="checkbox" [(ngModel)]="l.smsOptIn" /> SMS opt-in</label>
        }
        @if (l.partyType === 'None') { <label class="check"><input type="checkbox" [(ngModel)]="l.isCardLedger" /> Card / bank ledger for mixed payments</label> }
        <label class="field"><span>Opening balance</span><input type="number" class="num" [(ngModel)]="l.openingBalance" [disabled]="!auth.can('OpeningBalance', 'Edit')" /></label>
        <label class="field"><span>Dr / Cr</span><select [(ngModel)]="l.openingSide"><option value="Dr">Dr</option><option value="Cr">Cr</option></select></label>
        <label class="field span-2"><span>Remarks</span><input [(ngModel)]="l.remarks" /></label>
        <label class="check"><input type="checkbox" [(ngModel)]="l.isActive" [disabled]="l.isSystem" /> Active</label>
      </section></div>
    </div>`,
})
export class LedgerEditorComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  states = STATE_CODES;
  abs = Math.abs;
  l: any = {};
  groups = signal<{ id: number; name: string; systemCode?: string }[]>([]);
  areas = signal<LookupItem[]>([]);
  priceLists = signal<LookupItem[]>([]);
  schemes = signal<IdName[]>([]);
  employees = signal<Employee[]>([]);
  error = signal('');

  async ngOnInit(): Promise<void> {
    const [g, a, pl, s, e] = await Promise.all([
      this.api.get<any[]>('/account-groups'), this.api.get<LookupItem[]>('/lookups/area', { activeOnly: true }),
      this.api.get<LookupItem[]>('/lookups/pricelist', { activeOnly: true }), this.api.get<IdName[]>('/loyalty-schemes'), this.api.get<Employee[]>('/employees', { activeOnly: true }),
    ]);
    this.groups.set(g); this.areas.set(a); this.priceLists.set(pl); this.schemes.set(s); this.employees.set(e);
    const id = this.route.snapshot.paramMap.get('id');
    if (id && id !== 'new') this.l = await this.api.get(`/ledgers/${id}`);
    else {
      const type = this.route.snapshot.queryParamMap.get('type') ?? 'Customer';
      const code = type === 'Customer' ? 'SundryDebtors' : type === 'Supplier' ? 'SundryCreditors' : 'IndirectExpense';
      this.l = { id: 0, partyType: type, accountGroupId: g.find(x => x.systemCode === code)?.id, isActive: true, isCorporate: false, openingBalance: 0, openingSide: type === 'Supplier' ? 'Cr' : 'Dr',
        creditLimit: 0, creditDays: 0, discountPercent: 0, stateCode: this.auth.session()?.company.stateCode };
    }
  }

  onGstin(): void {
    this.l.gstin = normalizeGstin(this.l.gstin);
    if (this.l.gstin.length === 15 && !gstinError(this.l.gstin)) this.l.stateCode = this.l.gstin.substring(0, 2);
  }

  async save(): Promise<void> {
    this.error.set('');
    try {
      const body = { ...this.l, gstin: this.l.gstin || null, openingBalance: +this.l.openingBalance || 0 };
      const r = this.l.id ? await this.api.put<{ id: number }>(`/ledgers/${this.l.id}`, body) : await this.api.post<{ id: number }>('/ledgers', body);
      this.ui.ok('Saved.');
      this.l = await this.api.get(`/ledgers/${r.id}`);
      if (!this.route.snapshot.paramMap.get('id') || this.route.snapshot.paramMap.get('id') === 'new') this.router.navigate(['/masters/ledger', r.id], { replaceUrl: true });
    } catch (e) { this.error.set(errorMessage(e)); }
  }

  async remove(): Promise<void> {
    if (!(await this.ui.confirm('Delete', `Delete ${this.l.name}?`, { danger: true, okText: 'Delete' }))) return;
    try { await this.api.delete(`/ledgers/${this.l.id}`); this.ui.ok('Deleted.'); this.back(); } catch (e) { this.ui.error(errorMessage(e)); }
  }

  statement(): void { this.router.navigate(['/reports/ledger-statement'], { queryParams: { ledgerId: this.l.id, ledgerName: this.l.name } }); }
  back(): void { this.router.navigate(['/masters/parties', this.l.partyType ?? 'Customer']); }
}

/* ================= Account groups ================= */

interface Group { id: number; name: string; nature: string; isDirect: boolean; parentId?: number | null; parentName?: string; isSystem: boolean; isActive: boolean; systemCode?: string | null; }

@Component({
  selector: 'app-account-groups',
  standalone: true,
  imports: [FormsModule, ModalComponent],
  template: `
    <div class="page">
      <div class="page-header"><h1>Account groups</h1>@if (auth.can('AccountGroup', 'Add')) {<button class="btn primary" (click)="edit(null)">New group</button>}</div>
      <div class="table-wrap">
        <table class="grid">
          <thead><tr><th>Group</th><th>Nature</th><th>Parent</th><th>Direct (trading)</th><th></th></tr></thead>
          <tbody>
            @for (g of rows(); track g.id) {
              <tr class="clickable" [class.cancelled]="!g.isActive" (click)="edit(g)"><td><b>{{ g.name }}</b> @if (g.isSystem) {<span class="badge">system</span>}</td>
                <td>{{ g.nature }}</td><td>{{ g.parentName }}</td><td>{{ g.isDirect ? 'Yes' : '' }}</td>
                <td class="actions">@if (!g.isSystem && auth.can('AccountGroup', 'Delete')) {<button class="btn ghost sm" (click)="remove(g); $event.stopPropagation()">Delete</button>}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit group' : 'New group'" size="sm" (closed)="form.set(null)">
        <div class="stack">
          <label class="field req"><span>Name</span><input [(ngModel)]="f.name" autofocus /></label>
          <label class="field"><span>Nature</span><select [(ngModel)]="f.nature" [disabled]="f.isSystem"><option>Asset</option><option>Liability</option><option>Income</option><option>Expense</option></select></label>
          <label class="field"><span>Parent</span><select [(ngModel)]="f.parentId" [disabled]="f.isSystem"><option [ngValue]="null">— (top level)</option>
            @for (g of rows(); track g.id) { @if (g.id !== f.id && g.nature === f.nature) { <option [ngValue]="g.id">{{ g.name }}</option> } }</select></label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isDirect" /> Direct income/expense (trading account)</label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        <button footer class="btn primary" (click)="save(f)">Save</button>
      </app-modal>
    }`,
})
export class AccountGroupsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<Group[]>([]);
  form = signal<Group | null>(null);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> { try { this.rows.set(await this.api.get<Group[]>('/account-groups')); } catch (e) { this.ui.error(errorMessage(e)); } }
  edit(g: Group | null): void { this.form.set(g ? { ...g } : { id: 0, name: '', nature: 'Expense', isDirect: false, parentId: null, isSystem: false, isActive: true }); }
  async save(f: Group): Promise<void> {
    try { f.id ? await this.api.put(`/account-groups/${f.id}`, f) : await this.api.post('/account-groups', f); this.form.set(null); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(g: Group): Promise<void> {
    if (!(await this.ui.confirm('Delete group', `Delete ${g.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/account-groups/${g.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Employees ================= */

@Component({
  selector: 'app-employees',
  standalone: true,
  imports: [FormsModule, ModalComponent, MoneyPipe],
  template: `
    <div class="page">
      <div class="page-header"><h1>Employees &amp; salespersons</h1>@if (auth.can('Employee', 'Add')) {<button class="btn primary" (click)="edit(null)">New employee</button>}</div>
      <div class="table-wrap">
        <table class="grid">
          <thead><tr><th>Code</th><th>Name</th><th>Phone</th><th>Category</th><th>Shift</th><th class="num">Salary</th><th class="num">Commission %</th><th>Salesperson</th></tr></thead>
          <tbody>
            @for (e of rows(); track e.id) {
              <tr class="clickable" [class.cancelled]="!e.isActive" (click)="edit(e)"><td>{{ e.code }}</td><td><b>{{ e.name }}</b></td><td>{{ e.phone }}</td><td>{{ e.category }}</td>
                <td>{{ e.shiftStart }} – {{ e.shiftEnd }}</td><td class="num">{{ e.salary | money }}</td><td class="num">{{ e.commissionPercent }}</td><td>{{ e.isSalesperson ? 'Yes' : '' }}</td></tr>
            } @empty { <tr><td colspan="8" class="empty">No employees yet. Sales need at least one salesperson.</td></tr> }
          </tbody>
        </table>
      </div>
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit employee' : 'New employee'" (closed)="form.set(null)">
        <div class="form-grid cols-3">
          <label class="field"><span>Code</span><input [(ngModel)]="f.code" placeholder="auto" /></label>
          <label class="field req span-2"><span>Name</span><input [(ngModel)]="f.name" autofocus /></label>
          <label class="field"><span>Phone</span><input [(ngModel)]="f.phone" /></label>
          <label class="field"><span>Gender</span><select [(ngModel)]="f.gender"><option [ngValue]="null">—</option><option>Male</option><option>Female</option><option>Other</option></select></label>
          <label class="field"><span>Blood group</span><input [(ngModel)]="f.bloodGroup" /></label>
          <label class="field span-3"><span>Address</span><input [(ngModel)]="f.address" /></label>
          <label class="field"><span>Shift start</span><input type="time" [(ngModel)]="f.shiftStart" /></label>
          <label class="field"><span>Shift end</span><input type="time" [(ngModel)]="f.shiftEnd" /></label>
          <label class="field"><span>Category</span><input [(ngModel)]="f.category" /></label>
          <label class="field"><span>Salary</span><input type="number" class="num" [(ngModel)]="f.salary" /></label>
          <label class="field"><span>Commission %</span><input type="number" class="num" [(ngModel)]="f.commissionPercent" /></label>
          <span></span>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isSalesperson" /> Salesperson</label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        <div class="muted small" style="margin-top:8px">A matching ledger is created under “Employees” for salary and commission postings.</div>
        @if (f.id && auth.can('Employee', 'Delete')) { <button footer class="btn danger" (click)="remove(f)">Delete</button> }
        <button footer class="btn primary" (click)="save(f)">Save</button>
      </app-modal>
    }`,
})
export class EmployeesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<Employee[]>([]);
  form = signal<Employee | null>(null);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> { try { this.rows.set(await this.api.get<Employee[]>('/employees')); } catch (e) { this.ui.error(errorMessage(e)); } }
  edit(e: Employee | null): void {
    this.form.set(e ? { ...e, shiftStart: e.shiftStart?.substring(0, 5) ?? null, shiftEnd: e.shiftEnd?.substring(0, 5) ?? null }
      : { id: 0, code: '', name: '', salary: 0, commissionPercent: 0, isSalesperson: true, isActive: true });
  }
  async save(f: Employee): Promise<void> {
    const body = { ...f, shiftStart: f.shiftStart ? f.shiftStart.substring(0, 5) + ':00' : null, shiftEnd: f.shiftEnd ? f.shiftEnd.substring(0, 5) + ':00' : null };
    try { f.id ? await this.api.put(`/employees/${f.id}`, body) : await this.api.post('/employees', body); this.form.set(null); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(f: Employee): Promise<void> {
    if (!(await this.ui.confirm('Delete employee', `Delete ${f.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/employees/${f.id}`); this.form.set(null); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Simple lookups ================= */

const LOOKUP_TITLES: Record<string, string> = { area: 'Areas', category: 'Categories', manufacturer: 'Manufacturers', pricelist: 'Price lists', smsevent: 'SMS events' };

@Component({
  selector: 'app-lookups',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page" style="max-width:820px">
      <div class="page-header"><h1>{{ title() }}</h1></div>
      @if (auth.can('Masters', 'Add')) {
        <div class="card"><section class="row center-y">
          <input [(ngModel)]="newName" placeholder="New {{ title().toLowerCase() }} name" (keydown.enter)="add()" />
          <button class="btn primary" (click)="add()">Add</button>
        </section></div>
      }
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th>Name</th>@if (kind() === 'category') {<th>Show in POS</th><th class="num">Order</th><th>Colour</th>}<th>Active</th><th class="num">Used by</th><th></th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr>
                <td><input [(ngModel)]="r.name" /></td>
                @if (kind() === 'category') {
                  <td><input type="checkbox" [(ngModel)]="r.showInPos" /></td>
                  <td><input type="number" class="num" [(ngModel)]="r.sortOrder" style="width:70px" /></td>
                  <td><input type="color" [ngModel]="r.color || '#1f5fbf'" (ngModelChange)="r.color = $event" style="width:50px;padding:0" /></td>
                }
                <td><input type="checkbox" [(ngModel)]="r.isActive" /></td>
                <td class="num">{{ r.usageCount }}</td>
                <td class="actions"><button class="btn sm" (click)="save(r)" [disabled]="!auth.can('Masters', 'Edit')">Save</button>
                  <button class="btn ghost sm" (click)="remove(r)" [disabled]="!auth.can('Masters', 'Delete') || !!r.usageCount" [title]="r.usageCount ? 'In use — deactivate instead' : ''">Delete</button></td>
              </tr>
            } @empty { <tr><td colspan="7" class="empty">Nothing yet.</td></tr> }
          </tbody>
        </table>
      </div>
    </div>`,
})
export class LookupsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  kind = signal('category');
  title = computed(() => LOOKUP_TITLES[this.kind()] ?? this.kind());
  rows = signal<LookupItem[]>([]);
  newName = '';
  ngOnInit(): void { this.route.paramMap.subscribe(pm => { this.kind.set(pm.get('kind') ?? 'category'); this.load(); }); }
  async load(): Promise<void> { try { this.rows.set(await this.api.get<LookupItem[]>(`/lookups/${this.kind()}`)); } catch (e) { this.ui.error(errorMessage(e)); } }
  async add(): Promise<void> {
    if (!this.newName.trim()) return;
    try { await this.api.post(`/lookups/${this.kind()}`, { id: 0, name: this.newName.trim(), isActive: true, showInPos: true, sortOrder: 0 }); this.newName = ''; await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async save(r: LookupItem): Promise<void> {
    try { await this.api.put(`/lookups/${this.kind()}/${r.id}`, r); this.ui.ok('Saved.'); } catch (e) { this.ui.error(errorMessage(e)); await this.load(); }
  }
  async remove(r: LookupItem): Promise<void> {
    if (!(await this.ui.confirm('Delete', `Delete ${r.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/lookups/${this.kind()}/${r.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Loyalty ================= */

interface Scheme { id: number; name: string; saleAmount: number; points: number; targetPoints: number; pointValue: number; isActive: boolean; }

@Component({
  selector: 'app-loyalty',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="page" style="max-width:900px">
      <div class="page-header"><h1>Loyalty schemes</h1><a class="btn" routerLink="/reports/loyalty-points">Points report</a>
        @if (auth.can('Loyalty', 'Add')) {<button class="btn primary" (click)="add()">New scheme</button>}</div>
      @if (!auth.flag('EnableLoyalty')) { <div class="warn-box" style="margin-bottom:10px">Loyalty is switched off in Settings › Features.</div> }
      <div class="table-wrap">
        <table class="grid">
          <thead><tr><th>Name</th><th class="num">Every ₹</th><th class="num">earns points</th><th class="num">Redeem after</th><th class="num">₹ per point</th><th>Active</th><th></th></tr></thead>
          <tbody>
            @for (s of rows(); track $index) {
              <tr><td><input [(ngModel)]="s.name" /></td><td><input type="number" class="num" [(ngModel)]="s.saleAmount" /></td><td><input type="number" class="num" [(ngModel)]="s.points" /></td>
                <td><input type="number" class="num" [(ngModel)]="s.targetPoints" /></td><td><input type="number" class="num" [(ngModel)]="s.pointValue" /></td>
                <td><input type="checkbox" [(ngModel)]="s.isActive" /></td>
                <td class="actions"><button class="btn sm" (click)="save(s)">Save</button>@if (s.id) {<button class="btn ghost sm" (click)="remove(s)">Delete</button>}</td></tr>
            } @empty { <tr><td colspan="7" class="empty">No schemes.</td></tr> }
          </tbody>
        </table>
      </div>
      <p class="muted small">Points = bill amount ÷ “every ₹” × points. Customers can redeem once their balance reaches the target; each point is worth the ₹ value as a discount.</p>
    </div>`,
})
export class LoyaltyComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<Scheme[]>([]);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> { this.rows.set(await this.api.get<Scheme[]>('/loyalty-schemes')); }
  add(): void { this.rows.update(r => [...r, { id: 0, name: '', saleAmount: 100, points: 1, targetPoints: 100, pointValue: 1, isActive: true }]); }
  async save(s: Scheme): Promise<void> {
    try { s.id ? await this.api.put(`/loyalty-schemes/${s.id}`, s) : await this.api.post('/loyalty-schemes', s); this.ui.ok('Saved.'); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(s: Scheme): Promise<void> {
    if (!(await this.ui.confirm('Delete scheme', `Delete ${s.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/loyalty-schemes/${s.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Item offers ================= */

interface Offer { id: number; name: string; startAt: string; endAt: string; productId: number; productName?: string; batchId?: number | null; batchCode?: string; mrp: number; rate: number; discount: number; netRate: number; isActive: boolean; isLive?: boolean; _start?: string; _end?: string; }

@Component({
  selector: 'app-offers',
  standalone: true,
  imports: [FormsModule, LookupComponent, ModalComponent, MoneyPipe],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Item offers</h1>
        <label class="check"><input type="checkbox" [(ngModel)]="liveOnly" (ngModelChange)="load()" /> Live only</label>
        @if (auth.can('Offer', 'Add')) { <button class="btn" (click)="fill.set(true)">Fill category…</button> }
        <button class="btn primary" (click)="saveAll()" [disabled]="!auth.can('Offer', 'Edit')">Save changes</button></div>
      @if (!auth.flag('EnableItemOffers')) { <div class="warn-box" style="margin-bottom:10px">Item offers are switched off in Settings › Features.</div> }
      <div class="card" style="margin-bottom:10px"><section class="row center-y">
        <span>Add item:</span><div class="grow"><app-lookup [search]="searchProduct" [display]="pname" [clearOnPick]="true" placeholder="Search item" (picked)="addItem($event)" /></div>
      </section></div>
      <div class="table-wrap">
        <table class="grid">
          <thead><tr><th>Offer</th><th>Item</th><th>Batch</th><th>Starts</th><th>Ends</th><th class="num">MRP</th><th class="num">Rate</th><th class="num">Discount</th><th class="num">Net</th><th>Active</th><th></th></tr></thead>
          <tbody>
            @for (o of rows(); track $index) {
              <tr><td><input [(ngModel)]="o.name" /></td><td>{{ o.productName }} @if (o.isLive) {<span class="badge ok">live</span>}</td><td>{{ o.batchCode }}</td>
                <td><input type="datetime-local" [(ngModel)]="o._start" /></td><td><input type="datetime-local" [(ngModel)]="o._end" /></td>
                <td class="num">{{ o.mrp | money }}</td><td><input type="number" class="num" [(ngModel)]="o.rate" style="width:80px" /></td>
                <td><input type="number" class="num" [(ngModel)]="o.discount" style="width:80px" /></td><td class="num"><b>{{ o.rate - o.discount | money }}</b></td>
                <td><input type="checkbox" [(ngModel)]="o.isActive" /></td>
                <td class="actions">@if (o.id) {<button class="btn ghost sm" (click)="remove(o)">Delete</button>}</td></tr>
            } @empty { <tr><td colspan="11" class="empty">No offers.</td></tr> }
          </tbody>
        </table>
      </div>
    </div>
    @if (fill()) {
      <app-modal title="Create offers for a category" size="sm" (closed)="fill.set(false)">
        <div class="stack">
          <label class="field"><span>Offer name</span><input [(ngModel)]="f.name" /></label>
          <label class="field"><span>Category</span><select [(ngModel)]="f.categoryId"><option [ngValue]="null">All items</option>@for (c of categories(); track c.id) {<option [ngValue]="c.id">{{ c.name }}</option>}</select></label>
          <label class="field"><span>Starts</span><input type="datetime-local" [(ngModel)]="f.start" /></label>
          <label class="field"><span>Ends</span><input type="datetime-local" [(ngModel)]="f.end" /></label>
          <label class="field"><span>Discount %</span><input type="number" class="num" [(ngModel)]="f.discountPercent" /></label>
          <label class="field"><span>or discount ₹</span><input type="number" class="num" [(ngModel)]="f.discountAmount" /></label>
        </div>
        <button footer class="btn primary" (click)="doFill()">Create offers</button>
      </app-modal>
    }`,
})
export class OffersComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  rows = signal<Offer[]>([]);
  categories = signal<LookupItem[]>([]);
  fill = signal(false);
  liveOnly = false;
  f = { name: 'Offer', categoryId: null as number | null, start: toLocalInput(null), end: toLocalInput(new Date(Date.now() + 7 * 86400000).toISOString()), discountPercent: 10, discountAmount: 0 };
  pname = (p: ProductLookup) => `${p.name}${p.batchCode ? ' · ' + p.batchCode : ''}`;
  searchProduct = (t: string) => this.api.get<ProductLookup[]>('/products/lookup', { term: t, take: 20 });
  async ngOnInit(): Promise<void> { this.categories.set(await this.api.get<LookupItem[]>('/lookups/category')); await this.load(); }
  async load(): Promise<void> {
    const rows = await this.api.get<Offer[]>('/offers', { liveOnly: this.liveOnly });
    rows.forEach(r => { r._start = toLocalInput(r.startAt); r._end = toLocalInput(r.endAt); });
    this.rows.set(rows);
  }
  addItem(p: ProductLookup): void {
    this.rows.update(r => [{ id: 0, name: 'Offer', startAt: '', endAt: '', _start: toLocalInput(null), _end: toLocalInput(new Date(Date.now() + 7 * 86400000).toISOString()),
      productId: p.productId, productName: p.name, batchId: p.batchId, batchCode: p.batchCode ?? '', mrp: p.mrp, rate: p.saleRate, discount: 0, netRate: p.saleRate, isActive: true }, ...r]);
  }
  async saveAll(): Promise<void> {
    try {
      await this.api.put('/offers', this.rows().map(o => ({ ...o, startAt: fromLocalInput(o._start!), endAt: fromLocalInput(o._end!), netRate: o.rate - o.discount })));
      this.ui.ok('Offers saved.'); await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async doFill(): Promise<void> {
    try {
      const r = await this.api.post<{ count: number }>('/offers/fill', { ...this.f, startAt: fromLocalInput(this.f.start), endAt: fromLocalInput(this.f.end) });
      this.ui.ok(`${r.count} offers created.`); this.fill.set(false); await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(o: Offer): Promise<void> { try { await this.api.delete(`/offers/${o.id}`); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); } }
}

/* ================= Price lists ================= */

interface PriceRow { productId: number; code: string; name: string; purchaseRate: number; saleRate: number; mrp: number; price?: number | null; _orig?: number | null; }

@Component({
  selector: 'app-price-lists',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe],
  template: `
    <div class="page wide">
      <div class="page-header"><h1>Price lists</h1><a class="btn" routerLink="/masters/lookups/pricelist">Manage lists</a>
        <button class="btn primary" (click)="save()" [disabled]="!listId || !auth.can('PriceList', 'Edit')">Save {{ changed() || '' }} change(s)</button></div>
      @if (!auth.flag('EnablePriceList')) { <div class="warn-box" style="margin-bottom:10px">Price lists are switched off in Settings › Features.</div> }
      <div class="card"><section class="form-grid cols-6">
        <label class="field"><span>Price list</span><select [(ngModel)]="listId" (ngModelChange)="load()"><option [ngValue]="null">Choose…</option>@for (l of lists(); track l.id) {<option [ngValue]="l.id">{{ l.name }}</option>}</select></label>
        <label class="field"><span>Category</span><select [(ngModel)]="categoryId" (ngModelChange)="load()"><option [ngValue]="null">All</option>@for (c of categories(); track c.id) {<option [ngValue]="c.id">{{ c.name }}</option>}</select></label>
        <label class="field"><span>Search</span><input [(ngModel)]="search" (keydown.enter)="load()" /></label>
        <label class="field"><span>Bulk: base</span><select [(ngModel)]="bulk.base"><option value="SaleRate">Sale rate</option><option value="PurchaseRate">Purchase rate</option><option value="Mrp">MRP</option></select></label>
        <label class="field"><span>± value</span><div class="row"><input type="number" class="num" [(ngModel)]="bulk.value" /><label class="check"><input type="checkbox" [(ngModel)]="bulk.isPercent" /> %</label></div></label>
        <div class="field"><span>&nbsp;</span><button class="btn" (click)="applyBulk()" [disabled]="!listId">Apply to all listed</button></div>
      </section></div>
      <div class="table-wrap" style="margin-top:12px;max-height:calc(100vh - 270px)">
        <table class="grid">
          <thead><tr><th>Code</th><th>Item</th><th class="num">Purchase</th><th class="num">Sale</th><th class="num">MRP</th><th class="num" style="width:140px">List price</th></tr></thead>
          <tbody>
            @for (r of rows(); track r.productId) {
              <tr><td>{{ r.code }}</td><td>{{ r.name }}</td><td class="num">{{ r.purchaseRate | money }}</td><td class="num">{{ r.saleRate | money }}</td><td class="num">{{ r.mrp | money }}</td>
                <td><input type="number" class="num" [(ngModel)]="r.price" (ngModelChange)="t.set(t() + 1)" placeholder="—" /></td></tr>
            } @empty { <tr><td colspan="6" class="empty">Choose a price list.</td></tr> }
          </tbody>
        </table>
      </div>
    </div>`,
})
export class PriceListsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  lists = signal<LookupItem[]>([]);
  categories = signal<LookupItem[]>([]);
  rows = signal<PriceRow[]>([]);
  t = signal(0);
  listId: number | null = null;
  categoryId: number | null = null;
  search = '';
  bulk = { base: 'SaleRate', value: 0, isPercent: true };
  changed = computed(() => { this.t(); return this.rows().filter(r => (r.price ?? null) !== (r._orig ?? null)).length; });
  async ngOnInit(): Promise<void> {
    const [l, c] = await Promise.all([this.api.get<LookupItem[]>('/lookups/pricelist', { activeOnly: true }), this.api.get<LookupItem[]>('/lookups/category')]);
    this.lists.set(l); this.categories.set(c);
    if (l.length) { this.listId = l[0].id; await this.load(); }
  }
  async load(): Promise<void> {
    if (!this.listId) { this.rows.set([]); return; }
    const rows = await this.api.get<PriceRow[]>(`/price-lists/${this.listId}/items`, { categoryId: this.categoryId, search: this.search });
    rows.forEach(r => (r._orig = r.price ?? null));
    this.rows.set(rows); this.t.set(this.t() + 1);
  }
  applyBulk(): void {
    for (const r of this.rows()) {
      const b = this.bulk.base === 'PurchaseRate' ? r.purchaseRate : this.bulk.base === 'Mrp' ? r.mrp : r.saleRate;
      r.price = Math.max(0, r2(this.bulk.isPercent ? b + (b * this.bulk.value) / 100 : b + this.bulk.value));
    }
    this.t.set(this.t() + 1);
  }
  async save(): Promise<void> {
    try {
      await this.api.put(`/price-lists/${this.listId}/items`, this.rows().filter(r => (r.price ?? null) !== (r._orig ?? null)).map(r => ({ productId: r.productId, price: r.price === null || (r.price as any) === '' ? null : +r.price! })));
      this.ui.ok('Price list saved.'); await this.load();
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Bill of materials ================= */

interface BomLine { productId: number; productName?: string; unit?: string; kind: 'RawMaterial' | 'FinishedGood'; qty: number; isAltUnit: boolean; }
interface Bom { id: number; name: string; description?: string; finishedProductId?: number | null; batchSize: number; isActive: boolean; lines: BomLine[]; }

@Component({
  selector: 'app-boms',
  standalone: true,
  imports: [FormsModule, LookupComponent],
  template: `
    <div class="page">
      <div class="page-header"><h1>Bill of materials</h1>
        @if (auth.can('Bom', 'Add')) {<button class="btn" (click)="edit(null)">New BOM</button>}
        @if (cur()) { <button class="btn primary" (click)="save()">Save</button> }</div>
      @if (!auth.flag('EnableProduction')) { <div class="warn-box" style="margin-bottom:10px">Production is switched off in Settings › Features.</div> }
      <div class="row">
        <div class="card" style="width:260px"><header><h2>BOMs</h2></header>
          <section class="stack" style="gap:4px">@for (b of list(); track b.id) { <button class="link-btn" style="text-align:left" (click)="edit(b.id)">{{ b.name }}</button> } @empty {<span class="muted">None yet.</span>}</section></div>
        @if (cur(); as b) {
          <div class="card grow"><section class="stack">
            <div class="form-grid cols-3">
              <label class="field req"><span>Name</span><input [(ngModel)]="b.name" /></label>
              <label class="field"><span>Batch size (finished qty)</span><input type="number" class="num" [(ngModel)]="b.batchSize" /></label>
              <label class="check" style="align-self:end"><input type="checkbox" [(ngModel)]="b.isActive" /> Active</label>
              <label class="field span-all"><span>Description</span><input [(ngModel)]="b.description" /></label>
            </div>
            <table class="grid">
              <thead><tr><th>Item</th><th>Kind</th><th class="num">Qty</th><th>Unit</th><th></th></tr></thead>
              <tbody>
                @for (l of b.lines; track $index; let i = $index) {
                  <tr><td>{{ l.productName }}</td>
                    <td><select [(ngModel)]="l.kind"><option value="RawMaterial">Raw material</option><option value="FinishedGood">Finished good</option></select></td>
                    <td><input type="number" class="num" [(ngModel)]="l.qty" style="width:100px" /></td><td>{{ l.unit }}</td>
                    <td class="actions"><button class="btn ghost sm" (click)="b.lines.splice(i, 1)">✕</button></td></tr>
                }
              </tbody>
            </table>
            <div class="row center-y"><span>Add:</span><div class="grow"><app-lookup [search]="searchProduct" [display]="pname" [clearOnPick]="true" placeholder="Search item" (picked)="addLine($event)" /></div></div>
            @if (b.id && auth.can('Bom', 'Delete')) { <div><button class="btn danger" (click)="remove(b)">Delete BOM</button></div> }
          </section></div>
        }
      </div>
    </div>`,
})
export class BomsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  list = signal<Bom[]>([]);
  cur = signal<Bom | null>(null);
  pname = (p: ProductLookup) => p.name;
  searchProduct = (t: string) => this.api.get<ProductLookup[]>('/products/lookup', { term: t, take: 20 });
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> { this.list.set(await this.api.get<Bom[]>('/boms')); }
  async edit(id: number | null): Promise<void> {
    this.cur.set(id ? await this.api.get<Bom>(`/boms/${id}`) : { id: 0, name: '', batchSize: 1, isActive: true, lines: [] });
  }
  addLine(p: ProductLookup): void {
    this.cur()!.lines.push({ productId: p.productId, productName: p.name, unit: p.mainUnit, kind: this.cur()!.lines.length ? 'RawMaterial' : 'FinishedGood', qty: 1, isAltUnit: false });
  }
  async save(): Promise<void> {
    const b = this.cur()!;
    try {
      const r = b.id ? await this.api.put<{ id: number }>(`/boms/${b.id}`, b) : await this.api.post<{ id: number }>('/boms', b);
      this.ui.ok('BOM saved.'); await this.load(); await this.edit(r.id);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async remove(b: Bom): Promise<void> {
    if (!(await this.ui.confirm('Delete BOM', `Delete ${b.name}?`, { danger: true }))) return;
    try { await this.api.delete(`/boms/${b.id}`); this.cur.set(null); await this.load(); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Barcode labels ================= */

interface LabelTemplate { id: number; name: string; widthMm: number; heightMm: number; columns: number; gapMm: number; isDefault: boolean; fields: Record<string, any>; }
interface LabelRow { productId: number; batchId?: number | null; name: string; code: string; mrp: number; copies: number; }

@Component({
  selector: 'app-labels',
  standalone: true,
  imports: [FormsModule, LookupComponent, ModalComponent, MoneyPipe],
  template: `
    <div class="page">
      <div class="page-header"><h1>Barcode labels</h1>
        <select [(ngModel)]="templateId" style="width:220px">@for (t of templates(); track t.id) {<option [ngValue]="t.id">{{ t.name }}</option>}</select>
        <button class="btn" (click)="editTemplate()">Label design…</button>
        <button class="btn primary" (click)="print()" [disabled]="!rows().length">Print labels</button></div>
      <div class="card" style="margin-bottom:10px"><section class="row center-y">
        <div class="grow"><app-lookup [search]="searchProduct" [display]="pname" [clearOnPick]="true" placeholder="Scan or search item / batch" (picked)="add($event)" /></div>
        <button class="btn" (click)="fromDocument()">From purchase…</button>
      </section></div>
      <table class="grid">
        <thead><tr><th>Item</th><th>Barcode</th><th class="num">MRP</th><th class="num" style="width:120px">Copies</th><th></th></tr></thead>
        <tbody>
          @for (r of rows(); track $index; let i = $index) {
            <tr><td>{{ r.name }}</td><td class="mono">{{ r.code }}</td><td class="num">{{ r.mrp | money }}</td>
              <td><input type="number" class="num" [(ngModel)]="r.copies" /></td><td class="actions"><button class="btn ghost sm" (click)="rows().splice(i, 1)">✕</button></td></tr>
          } @empty { <tr><td colspan="5" class="empty">Add items to print labels.</td></tr> }
        </tbody>
      </table>
    </div>
    @if (tpl(); as t) {
      <app-modal title="Label design" size="sm" (closed)="tpl.set(null)">
        <div class="form-grid cols-2">
          <label class="field span-2"><span>Name</span><input [(ngModel)]="t.name" /></label>
          <label class="field"><span>Width mm</span><input type="number" class="num" [(ngModel)]="t.widthMm" /></label>
          <label class="field"><span>Height mm</span><input type="number" class="num" [(ngModel)]="t.heightMm" /></label>
          <label class="field"><span>Labels across</span><input type="number" class="num" [(ngModel)]="t.columns" /></label>
          <label class="field"><span>Gap mm</span><input type="number" class="num" [(ngModel)]="t.gapMm" /></label>
          <label class="field"><span>Font size</span><input type="number" class="num" [(ngModel)]="t.fields['fontSize']" /></label>
          <span></span>
          @for (k of fieldKeys; track k[0]) { <label class="check"><input type="checkbox" [(ngModel)]="t.fields[k[0]]" /> {{ k[1] }}</label> }
          <label class="check"><input type="checkbox" [(ngModel)]="t.isDefault" /> Default</label>
        </div>
        <button footer class="btn primary" (click)="saveTemplate(t)">Save</button>
      </app-modal>
    }`,
})
export class LabelsComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  templates = signal<LabelTemplate[]>([]);
  rows = signal<LabelRow[]>([]);
  tpl = signal<LabelTemplate | null>(null);
  templateId: number | null = null;
  fieldKeys: [string, string][] = [['showCompany', 'Company name'], ['showName', 'Item name'], ['showMrp', 'MRP'], ['showSaleRate', 'Sale rate'],
    ['showBarcode', 'Barcode'], ['showBarcodeText', 'Barcode text'], ['showExpiry', 'Expiry'], ['showPackedDate', 'Packed date']];
  pname = (p: ProductLookup) => `${p.name}${p.batchCode ? ' · ' + p.batchCode : ''}`;
  searchProduct = (t: string) => this.api.get<ProductLookup[]>('/products/lookup', { term: t, take: 20 });
  async ngOnInit(): Promise<void> {
    await this.loadTemplates();
    // Sent here from the product screen / product list: one row per item, printing its linked barcode (else its code).
    const ids = (this.route.snapshot.queryParamMap.get('productIds') ?? '').split(',').map(Number).filter(n => n > 0);
    if (!ids.length) return;
    try {
      const items = await this.ui.run(() => Promise.all(ids.map(id => this.api.get<any>(`/products/${id}`))));
      this.rows.update(r => [...r, ...items.filter(p => p.itemClass !== 'Service')
        .map(p => ({ productId: p.id, batchId: null, name: p.name, code: p.defaultBarcode || p.code, mrp: p.mrp, copies: 1 }))]);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  private async loadTemplates(): Promise<void> {
    const t = await this.api.get<LabelTemplate[]>('/print/label-templates');
    this.templates.set(t);
    this.templateId ??= t.find(x => x.isDefault)?.id ?? t[0]?.id ?? null;
  }
  add(p: ProductLookup): void {
    this.rows.update(r => [...r, { productId: p.productId, batchId: p.batchId, name: p.name, code: p.batchCode ?? p.code, mrp: p.mrp, copies: Math.max(1, Math.floor(p.stock) || 1) }]);
  }
  async fromDocument(): Promise<void> {
    const no = await this.ui.prompt('Labels from a purchase', 'Purchase document id (open the purchase and read its id from the address bar)', { type: 'number' });
    if (!no) return;
    try {
      const d = await this.api.get<any>(`/documents/${no}`);
      for (const l of d.lines) this.rows.update(r => [...r, { productId: l.productId, batchId: l.batchId, name: l.productName, code: l.batchCode ?? l.productCode, mrp: l.mrp, copies: Math.ceil((l.qty + l.freeQty) * l.unitFactor) }]);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async print(): Promise<void> {
    try { await this.api.openPdf('/print/labels', { templateId: this.templateId, items: this.rows().map(r => ({ productId: r.productId, batchId: r.batchId, copies: +r.copies || 0 })) }); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  editTemplate(): void {
    const t = this.templates().find(x => x.id === this.templateId);
    this.tpl.set(t ? JSON.parse(JSON.stringify(t)) : { id: 0, name: 'New label', widthMm: 50, heightMm: 25, columns: 2, gapMm: 2, isDefault: false, fields: { fontSize: 7, showName: true, showBarcode: true, showMrp: true } });
  }
  async saveTemplate(t: LabelTemplate): Promise<void> {
    try { const r = await this.api.post<{ id: number }>('/print/label-templates', t); this.templateId = r.id; this.tpl.set(null); await this.loadTemplates(); this.ui.ok('Saved.'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}
