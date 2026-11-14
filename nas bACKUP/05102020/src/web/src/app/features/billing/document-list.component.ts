import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { DmyhmPipe, MoneyPipe, monthStart, today } from '../../core/format';
import { DocumentListItem, Paged, SCREEN_FOR, VOUCHER_LABELS, VoucherType } from '../../core/models';
import { Ui } from '../../core/ui';
import { PagerComponent } from '../../shared/components';

const FAMILIES: Record<string, VoucherType[]> = {
  Sales: ['Sales', 'POSSales', 'LocalSales', 'SalesReturn', 'Estimate', 'SalesOrder', 'DeliveryNote'],
  Purchase: ['Purchase', 'LocalPurchase', 'PurchaseReturn', 'PurchaseOrder', 'ReceiptNote'],
  Inventory: ['OpeningStock', 'StockAdjustment', 'Production'],
};

@Component({
  selector: 'app-document-list',
  standalone: true,
  imports: [FormsModule, RouterLink, MoneyPipe, DmyhmPipe, PagerComponent],
  template: `
    <div class="page wide">
      <div class="page-header">
        <h1>Search {{ family() }} documents</h1>
        @if (auth.can(screenOf(type), 'Add')) { <a class="btn primary" [routerLink]="['/doc', type, 'new']">New {{ labels[type] }}</a> }
      </div>
      <div class="card"><section>
        <div class="form-grid cols-6">
          <label class="field"><span>Type</span>
            <select [(ngModel)]="type" (ngModelChange)="load(1)">
              @for (t of types(); track t) { @if (auth.can(screenOf(t))) { <option [value]="t">{{ labels[t] }}</option> } }
            </select></label>
          <label class="field"><span>From</span><input type="date" [(ngModel)]="from" (change)="load(1)" /></label>
          <label class="field"><span>To</span><input type="date" [(ngModel)]="to" (change)="load(1)" /></label>
          <label class="field span-2"><span>Search (bill no, party, phone, ref)</span><input [(ngModel)]="search" (keydown.enter)="load(1)" /></label>
          <label class="field"><span>Status</span>
            <select [(ngModel)]="status" (ngModelChange)="load(1)"><option value="">All</option><option value="active">Active</option><option value="cancelled">Cancelled</option><option value="open">Open orders</option></select></label>
        </div>
      </section></div>
      <div class="table-wrap" style="margin-top:12px">
        <table class="grid">
          <thead><tr><th>Date</th><th>No</th><th>Party</th><th>Mode</th><th class="num">Taxable</th><th class="num">Tax</th><th class="num">Amount</th><th>User</th><th>Counter</th><th>Status</th></tr></thead>
          <tbody>
            @for (d of rows(); track d.id) {
              <tr class="clickable" [class.cancelled]="d.cancelled" (click)="open(d)">
                <td>{{ d.date | dmyhm }}</td><td><b>{{ d.documentNo }}</b></td><td>{{ d.partyName }}</td><td>{{ d.paymentMode }}</td>
                <td class="num">{{ d.taxableAmount | money }}</td><td class="num">{{ d.taxAmount | money }}</td><td class="num"><b>{{ d.billAmount | money }}</b></td>
                <td>{{ d.userName }}</td><td>{{ d.counter }}</td>
                <td>@if (d.cancelled) {<span class="badge danger">Cancelled</span>} @else if (d.isProcessed) {<span class="badge ok">Converted</span>}</td>
              </tr>
            } @empty { <tr><td colspan="10" class="empty">No documents.</td></tr> }
          </tbody>
          <tfoot><tr><td colspan="6">Page total</td><td class="num">{{ pageTotal() | money }}</td><td colspan="3"></td></tr></tfoot>
        </table>
      </div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="50" (go)="load($event)" />
    </div>`,
})
export class DocumentListComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  labels = VOUCHER_LABELS;
  family = signal('Sales');
  types = computed(() => FAMILIES[this.family()] ?? [this.family() as VoucherType]);
  type: VoucherType = 'Sales';
  from = monthStart();
  to = today();
  search = '';
  status = '';
  rows = signal<DocumentListItem[]>([]);
  total = signal(0);
  page = signal(1);
  pageTotal = computed(() => this.rows().filter(r => !r.cancelled).reduce((s, r) => s + r.billAmount, 0));

  screenOf(t: VoucherType): string { return SCREEN_FOR[t] ?? 'Sales'; }

  ngOnInit(): void {
    this.route.paramMap.subscribe(pm => {
      const t = pm.get('type') ?? 'Sales';
      const fam = Object.keys(FAMILIES).find(k => k === t || FAMILIES[k].includes(t as VoucherType)) ?? 'Sales';
      this.family.set(fam);
      this.type = (FAMILIES[fam].includes(t as VoucherType) ? t : FAMILIES[fam].find(x => this.auth.can(this.screenOf(x))) ?? FAMILIES[fam][0]) as VoucherType;
      this.load(1);
    });
  }

  async load(page: number): Promise<void> {
    try {
      const r = await this.api.get<Paged<DocumentListItem>>('/documents', {
        type: this.type, from: this.from, to: this.to, search: this.search, page, pageSize: 50,
        cancelled: this.status === 'cancelled' ? true : this.status === 'active' ? false : undefined,
        openOrders: this.status === 'open' ? true : undefined,
      });
      this.rows.set(r.items);
      this.total.set(r.total);
      this.page.set(page);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  open(d: DocumentListItem): void { this.router.navigate(['/doc', d.voucherType, d.id]); }
}
