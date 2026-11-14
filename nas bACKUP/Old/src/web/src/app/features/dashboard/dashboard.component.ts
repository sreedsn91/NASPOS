import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { DmyPipe, MoneyPipe, QtyPipe } from '../../core/format';
import { Live } from '../../core/live';
import { Ui } from '../../core/ui';
import { IconComponent } from '../../shared/icon';

interface Dashboard {
  todaySales: number; todayBills: number; monthSales: number; yearSales: number; yearPurchase: number; cashBalance: number; bankBalance: number;
  receivables: number; payables: number; lowStockItems: number; expiringBatches: number;
  months: { month: string; sales: number; purchase: number; orders: number }[];
  counters: { counter: string; bills: number; cash: number; card: number; credit: number; total: number }[];
  topItems: { name: string; qty: number; amount: number }[];
  openOrders: { id: number; documentNo: string; voucherType: string; date: string; party?: string; amount: number; deliveryDate?: string }[];
  reminders: { kind: string; text: string; link?: string }[];
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [MoneyPipe, QtyPipe, DmyPipe, RouterLink, IconComponent],
  template: `
    <div class="page wide">
      <div class="welcome">
        <div class="grow">
          <h2>{{ greeting() }}, {{ auth.session()?.user?.displayName || auth.session()?.user?.userName }}</h2>
          <p>{{ auth.session()?.company?.name }} · FY {{ auth.session()?.financialYear?.name }}</p>
        </div>
        @if (auth.can('POS', 'Add')) { <a class="btn primary" routerLink="/pos"><app-icon name="zap" [size]="16" /> Touch POS <kbd>F9</kbd></a> }
        @if (auth.can('Sales', 'Add')) { <a class="btn" routerLink="/doc/Sales/new"><app-icon name="receipt" [size]="16" /> New invoice</a> }
        @if (auth.can('Purchase', 'Add')) { <a class="btn" routerLink="/doc/Purchase/new"><app-icon name="truck" [size]="16" /> New purchase</a> }
        @if (auth.can('Dashboard')) { <button class="btn icon" (click)="load()" title="Refresh" aria-label="Refresh">↻</button> }
      </div>
      @if (!auth.can('Dashboard')) {
        <div class="info-box"><app-icon name="info" [size]="18" /> Use the menu (or press Ctrl+K) to start billing.</div>
      } @else { @if (d(); as d) {
        <div class="kpis">
          <div class="kpi"><div class="k-icon"><app-icon name="cart" /></div><div><div class="k-label">Today's sales</div><div class="k-value">₹ {{ d.todaySales | money }}</div><div class="k-sub">{{ d.todayBills }} bills</div></div></div>
          <div class="kpi"><div class="k-icon violet"><app-icon name="calendar" /></div><div><div class="k-label">This month</div><div class="k-value">₹ {{ d.monthSales | money }}</div></div></div>
          <div class="kpi"><div class="k-icon ok"><app-icon name="trend" /></div><div><div class="k-label">Year sales</div><div class="k-value">₹ {{ d.yearSales | money }}</div><div class="k-sub">Purchase ₹ {{ d.yearPurchase | money }}</div></div></div>
          <div class="kpi"><div class="k-icon"><app-icon name="cash" /></div><div><div class="k-label">Cash in hand</div><div class="k-value">₹ {{ d.cashBalance | money }}</div><div class="k-sub">Bank ₹ {{ d.bankBalance | money }}</div></div></div>
          <div class="kpi"><div class="k-icon warn"><app-icon name="users" /></div><div><div class="k-label">Receivables</div><div class="k-value">₹ {{ d.receivables | money }}</div><div class="k-sub">Payables ₹ {{ d.payables | money }}</div></div></div>
          <div class="kpi"><div class="k-icon rose"><app-icon name="bell" /></div><div><div class="k-label">Stock alerts</div><div class="k-value">{{ d.lowStockItems }}</div><div class="k-sub">{{ d.expiringBatches }} batches expiring</div></div></div>
        </div>
        <div class="cols">
          <div class="card" style="grid-column: span 2">
            <header><h2>Sales vs purchase — financial year</h2>
              <div class="legend"><span><i style="background:var(--primary)"></i>Sales</span><span><i style="background:#8ba6cf"></i>Purchase</span></div></header>
            <section>
              <div class="bars" role="img" aria-label="Monthly sales and purchase chart">
                @for (m of d.months; track m.month) {
                  <div class="bar-col" [title]="m.month + ': sales ' + (m.sales | money) + ', purchase ' + (m.purchase | money)">
                    <div class="bar-stack">
                      <div class="bar s" [style.height.%]="pct(m.sales)"></div>
                      <div class="bar p" [style.height.%]="pct(m.purchase)"></div>
                    </div>
                    <div class="bar-label">{{ m.month }}</div>
                  </div>
                }
              </div>
            </section>
          </div>
          <div class="card">
            <header><h2>Reminders</h2></header>
            <section class="stack">
              @for (r of d.reminders; track $index) {
                <div class="row center-y"><span class="badge warn">{{ r.kind }}</span>
                  @if (r.link) { <button class="link-btn" (click)="follow(r.link)">{{ r.text }}</button> } @else { <span>{{ r.text }}</span> }</div>
              } @empty { <div class="muted">Nothing needs attention.</div> }
            </section>
          </div>
          <div class="card">
            <header><h2>Counters today</h2><span class="badge" [class.ok]="live.connected()">live</span></header>
            <table class="grid">
              <thead><tr><th>Counter</th><th class="num">Bills</th><th class="num">Cash</th><th class="num">Card</th><th class="num">Credit</th><th class="num">Total</th></tr></thead>
              <tbody>
                @for (c of d.counters; track c.counter) {
                  <tr><td>{{ c.counter }}</td><td class="num">{{ c.bills }}</td><td class="num">{{ c.cash | money }}</td><td class="num">{{ c.card | money }}</td>
                    <td class="num">{{ c.credit | money }}</td><td class="num"><b>{{ c.total | money }}</b></td></tr>
                } @empty { <tr><td colspan="6" class="empty">No bills yet today.</td></tr> }
              </tbody>
            </table>
          </div>
          <div class="card">
            <header><h2>Top items this month</h2></header>
            <table class="grid">
              <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Amount</th></tr></thead>
              <tbody>
                @for (t of d.topItems; track t.name) { <tr><td>{{ t.name }}</td><td class="num">{{ t.qty | qty }}</td><td class="num">{{ t.amount | money }}</td></tr> }
                @empty { <tr><td colspan="3" class="empty">No sales this month.</td></tr> }
              </tbody>
            </table>
          </div>
          <div class="card">
            <header><h2>Open orders</h2></header>
            <table class="grid">
              <thead><tr><th>No</th><th>Party</th><th>Delivery</th><th class="num">Amount</th></tr></thead>
              <tbody>
                @for (o of d.openOrders; track o.id) {
                  <tr class="clickable" (click)="router.navigate(['/doc', o.voucherType, o.id])"><td>{{ o.documentNo }}</td><td>{{ o.party }}</td>
                    <td>{{ o.deliveryDate | dmy }}</td><td class="num">{{ o.amount | money }}</td></tr>
                } @empty { <tr><td colspan="4" class="empty">No open orders.</td></tr> }
              </tbody>
            </table>
          </div>
        </div>
      } @else if (error()) { <div class="error-box">{{ error() }}</div> } }
    </div>`,
})
export class DashboardComponent implements OnInit, OnDestroy {
  auth = inject(Auth);
  live = inject(Live);
  router = inject(Router);
  private api = inject(Api);
  private ui = inject(Ui);
  d = signal<Dashboard | null>(null);
  error = signal('');
  private max = computed(() => Math.max(1, ...(this.d()?.months ?? []).flatMap(m => [m.sales, m.purchase])));
  private sub?: Subscription;
  private timer: any;

  ngOnInit(): void {
    this.load();
    // Live counter dashboard: refresh (debounced) whenever any counter saves a document.
    this.sub = this.live.documentSaved.subscribe(() => { clearTimeout(this.timer); this.timer = setTimeout(() => this.load(), 800); });
  }
  ngOnDestroy(): void { this.sub?.unsubscribe(); clearTimeout(this.timer); }

  greeting(): string { const h = new Date().getHours(); return h < 12 ? 'Good morning' : h < 17 ? 'Good afternoon' : 'Good evening'; }

  pct(v: number): number { return Math.round((v / this.max()) * 100); }

  async load(): Promise<void> {
    if (!this.auth.can('Dashboard')) return;
    try { this.d.set(await this.api.get<Dashboard>('/dashboard')); this.error.set(''); } catch (e) { this.error.set(errorMessage(e)); }
  }

  follow(link: string): void {
    const [kind, arg] = link.split(':');
    if (kind === 'report') this.router.navigate(['/reports', arg]);
    else if (kind === 'voucher') this.router.navigate(['/voucher', 'Receipt', arg]);
    else if (kind === 'offers') this.router.navigate(['/masters/offers']);
    else if (kind === 'messaging') this.router.navigate(['/settings/messaging']);
    else this.ui.info(link);
  }
}
