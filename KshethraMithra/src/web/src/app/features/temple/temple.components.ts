import { Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Ui } from '../../core/ui';
import { DmyPipe, DmyhmPipe, MoneyPipe, QtyPipe, today } from '../../core/format';
import { HotkeysComponent, LookupComponent, ModalComponent, PagerComponent } from '../../shared/components';
import { ManglishDirective } from '../../shared/manglish';

/* ================= Shared types & helpers ================= */

export interface Named { id: number; en: string; ml: string; }
export interface CalendarNames { stars: Named[]; months: Named[]; tithis: Named[]; weekdays: Named[]; }
export interface CalendarDay {
  date: string; weekday: string; weekdayMl: string; malayalamYear: number; monthSign: number; month: string; monthMl: string;
  malayalamDay: number; malayalamDate: string; star: number; starName: string; starNameMl: string; extraStar: number | null; extraStarName: string | null;
  starAtSunrise: number; starAtSunriseName: string; starAtSunriseEnds: string; tithi: number; tithiName: string; tithiNameMl: string; tithiEnds: string;
  sunrise: string; sunset: string; sankranti: string | null; festival: string | null; corrected: boolean; highlights: string[];
}
interface Deity { id: number; name: string; regionalName: string | null; sortOrder: number; isActive: boolean; }
interface PoojaSlot { id: number; name: string; regionalName: string | null; time: string | null; sortOrder: number; isActive: boolean; }
interface Offering {
  id: number; code: string; name: string; regionalName: string | null; rate: number; deityId: number | null; deityName: string | null;
  poojaSlotId: number | null; poojaSlotName: string | null; needsName: boolean; needsStar: boolean; categoryId: number | null;
}
interface Devotee {
  id: number; name: string; regionalName: string | null; star: number | null; starName: string | null; houseName: string | null; phone: string | null;
  address: string | null; city: string | null; email: string | null; dateOfBirth: string | null; notes: string | null; isActive: boolean;
}
interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; }

type Kind = 'Once' | 'Daily' | 'Weekly' | 'StarDay' | 'MalayalamDate' | 'Tithi';
export interface Schedule {
  kind: Kind; start: string; end: string | null; count: number | null; weekday: string | null; star: number | null;
  malayalamDay: number | null; monthSign: number | null; tithi: number | null; paksha: 'Both' | 'Shukla' | 'Krishna';
}
interface PreviewDate { date: string; weekday: string; malayalamDate: string; star: string; tithi: string; }
interface SchedulePreview { description: string; count: number; dates: PreviewDate[]; amount: number | null; }

const KINDS: { id: Kind; label: string }[] = [
  { id: 'Once', label: 'One day' },
  { id: 'Weekly', label: 'Every week on a day (e.g. every Friday)' },
  { id: 'StarDay', label: 'Every star day (e.g. every Karthika)' },
  { id: 'MalayalamDate', label: 'A Malayalam date (e.g. every month 1st)' },
  { id: 'Tithi', label: 'A tithi (Ekadashi, Pournami, Amavasi…)' },
  { id: 'Daily', label: 'Every day' },
];

const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

let namesCache: Promise<CalendarNames> | null = null;
/** Star / month / tithi names for pickers (loaded once). */
export function calendarNames(api: Api): Promise<CalendarNames> {
  namesCache ??= api.get<CalendarNames>('/calendar/names').catch(e => { namesCache = null; throw e; });
  return namesCache;
}

export function newSchedule(start = today()): Schedule {
  return { kind: 'Once', start, end: null, count: null, weekday: null, star: null, malayalamDay: null, monthSign: null, tithi: null, paksha: 'Both' };
}

/** Only the fields that matter for the kind are sent. */
export function scheduleBody(s: Schedule): Record<string, unknown> {
  const b: Record<string, unknown> = { kind: s.kind, start: s.start };
  if (s.kind !== 'Once') { b['end'] = s.end || null; b['count'] = s.count || null; }
  if (s.kind === 'Weekly') b['weekday'] = s.weekday;
  if (s.kind === 'StarDay') { b['star'] = s.star; b['monthSign'] = s.monthSign; }
  if (s.kind === 'MalayalamDate') { b['malayalamDay'] = s.malayalamDay; b['monthSign'] = s.monthSign; }
  if (s.kind === 'Tithi') { b['tithi'] = s.tithi; b['paksha'] = s.paksha; }
  return b;
}

function addDays(iso: string, days: number): string {
  const d = new Date(iso + 'T00:00:00');
  d.setDate(d.getDate() + days);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/* ================= Schedule editor ================= */

/** Picks when an offering is performed: one day, every Friday, every Karthika, a Malayalam date or a tithi. */
@Component({
  selector: 'app-schedule-editor',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (names(); as n) {
      <div class="form-grid cols-3">
        <label class="field span-3"><span>When</span>
          <select [(ngModel)]="s.kind" (ngModelChange)="kindChanged()">@for (k of kinds; track k.id) {<option [ngValue]="k.id">{{ k.label }}</option>}</select></label>
        <label class="field"><span>{{ s.kind === 'Once' ? 'Date' : 'From' }}</span><input type="date" [(ngModel)]="s.start" (ngModelChange)="changed.emit()" /></label>
        @if (s.kind !== 'Once') {
          <label class="field"><span>Until</span><input type="date" [(ngModel)]="s.end" (ngModelChange)="changed.emit()" /></label>
          <label class="field"><span>or number of times</span><input type="number" min="1" class="num" [(ngModel)]="s.count" (ngModelChange)="changed.emit()" /></label>
        }
        @switch (s.kind) {
          @case ('Weekly') {
            <label class="field"><span>Day</span><select [(ngModel)]="s.weekday" (ngModelChange)="changed.emit()">
              @for (w of weekdays; track w; let i = $index) {<option [ngValue]="w">{{ w }} · {{ n.weekdays[i].ml }}</option>}</select></label>
          }
          @case ('StarDay') {
            <label class="field"><span>Star</span><select [(ngModel)]="s.star" (ngModelChange)="changed.emit()">
              @for (x of n.stars; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select></label>
            <label class="field"><span>Only in month</span><select [(ngModel)]="s.monthSign" (ngModelChange)="changed.emit()"><option [ngValue]="null">Every month</option>
              @for (x of n.months; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select></label>
          }
          @case ('MalayalamDate') {
            <label class="field"><span>Malayalam date</span><input type="number" min="1" max="32" class="num" [(ngModel)]="s.malayalamDay" (ngModelChange)="changed.emit()" /></label>
            <label class="field"><span>Only in month</span><select [(ngModel)]="s.monthSign" (ngModelChange)="changed.emit()"><option [ngValue]="null">Every month</option>
              @for (x of n.months; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select></label>
          }
          @case ('Tithi') {
            <label class="field"><span>Tithi</span><select [(ngModel)]="s.tithi" (ngModelChange)="changed.emit()">
              @for (x of n.tithis; track x.id) {<option [ngValue]="x.id">{{ x.id === 15 ? 'Pournami / Amavasi' : x.en }} · {{ x.ml }}</option>}</select></label>
            <label class="field"><span>Fortnight</span><select [(ngModel)]="s.paksha" (ngModelChange)="changed.emit()">
              <option value="Both">Both (bright and dark)</option><option value="Shukla">Shukla (bright){{ s.tithi === 15 ? ' — Pournami' : '' }}</option>
              <option value="Krishna">Krishna (dark){{ s.tithi === 15 ? ' — Amavasi' : '' }}</option></select></label>
          }
        }
      </div>
    }`,
})
export class ScheduleEditorComponent implements OnInit {
  @Input({ required: true }) s!: Schedule;
  @Output() changed = new EventEmitter<void>();
  private api = inject(Api);
  names = signal<CalendarNames | null>(null);
  kinds = KINDS;
  weekdays = WEEKDAYS;
  async ngOnInit(): Promise<void> { this.names.set(await calendarNames(this.api)); }
  kindChanged(): void {
    const s = this.s;
    if (s.kind !== 'Once' && !s.end && !s.count) s.end = addDays(s.start, 89);
    if (s.kind === 'Weekly' && !s.weekday) s.weekday = WEEKDAYS[new Date(s.start + 'T00:00:00').getDay()];
    if (s.kind === 'Tithi' && !s.tithi) s.tithi = 11;
    if (s.kind === 'MalayalamDate' && !s.malayalamDay) s.malayalamDay = 1;
    this.changed.emit();
  }
}

/* ================= Printing a booking ================= */

/** What to print for a booking: cut slips per offering, and how many duplicate receipt copies. */
export interface PrintChoice { slips: boolean; duplicates: number; }

/** The print choice from Temple › Vazhipadu Print Settings (cut print, duplicate receipt and its copies). */
function defaultPrintChoice(auth: Auth): PrintChoice {
  const copies = Math.min(9, Math.max(1, Number(auth.setting('VazhipaduDuplicateCopies')) || 1));
  return { slips: auth.flag('VazhipaduItemSlips'), duplicates: auth.flag('VazhipaduDuplicate') ? copies : 0 };
}

/**
 * Prints a booking: the receipt with every offering, its duplicate copies, then (with cut print) each offering's slip.
 * With a receipt printer set in Temple › Vazhipadu Print Settings, the server prints straight to it with a cut after every
 * piece and no print dialog; otherwise the browser prints the receipt (with duplicates) and each slip as its own job.
 */
async function printBooking(api: Api, auth: Auth, bookingId: number, lineCount: number, choice: PrintChoice): Promise<void> {
  if (auth.setting('ReceiptPrinter').trim()) {
    await api.post(`/vazhipadu/bookings/${bookingId}/print`, {}, { slips: choice.slips, duplicates: choice.duplicates });
    return;
  }
  await api.printPdf(`/vazhipadu/bookings/${bookingId}/receipt`, { slips: false, duplicates: choice.duplicates });
  if (choice.slips) for (let n = 1; n <= lineCount; n++) await api.printPdf(`/vazhipadu/bookings/${bookingId}/slips/${n}`);
}

interface PrintAsk { id: number; bookingNo: string; lineCount: number; }

/** Print confirmation: cut print and duplicates, preset from the settings, chosen before a booking prints. */
@Component({
  selector: 'app-print-options',
  standalone: true,
  imports: [FormsModule, ModalComponent],
  template: `
    <app-modal [title]="'Print receipt ' + bookingNo" size="sm" (closed)="cancelled.emit()">
      <div class="stack">
        <label class="check"><input type="checkbox" [(ngModel)]="slips" /> Cut print: a separate slip for each offering ({{ lineCount }})</label>
        <label class="check"><input type="checkbox" [(ngModel)]="duplicate" /> Duplicate receipt</label>
        @if (duplicate) {
          <label class="field"><span>Number of duplicates</span>
            <input type="number" class="num" min="1" max="9" [(ngModel)]="copies" style="max-width:120px" /></label>
        }
      </div>
      <button footer class="btn" (click)="cancelled.emit()">Don't print</button>
      <button footer class="btn primary" (click)="ok()">Print</button>
    </app-modal>`,
})
export class PrintOptionsComponent implements OnInit {
  private auth = inject(Auth);
  @Input({ required: true }) bookingNo = '';
  @Input() lineCount = 0;
  @Output() print = new EventEmitter<PrintChoice>();
  @Output() cancelled = new EventEmitter<void>();
  slips = true;
  duplicate = false;
  copies = 1;
  ngOnInit(): void {
    const d = defaultPrintChoice(this.auth);
    this.slips = d.slips;
    this.duplicate = d.duplicates > 0;
    this.copies = d.duplicates || Math.min(9, Math.max(1, Number(this.auth.setting('VazhipaduDuplicateCopies')) || 1));
  }
  ok(): void {
    const copies = Math.min(9, Math.max(1, Math.floor(Number(this.copies)) || 1));
    this.print.emit({ slips: this.slips, duplicates: this.duplicate ? copies : 0 });
  }
}

/* ================= Vazhipadu counter ================= */

interface CartLine {
  key: number; offering: Offering; devoteeName: string; star: number | null; qty: number; remarks: string;
  schedule: Schedule; count: number; description: string; dates: PreviewDate[]; error: string | null;
}

@Component({
  selector: 'app-vazhipadu',
  standalone: true,
  imports: [FormsModule, RouterLink, ModalComponent, LookupComponent, HotkeysComponent, MoneyPipe, DmyPipe, ScheduleEditorComponent, PrintOptionsComponent],
  styles: [`
    /* Offerings take the narrower side; the selected items (cart) get the room. */
    .counter { display: grid; grid-template-columns: minmax(300px, 2fr) minmax(0, 3fr); gap: 14px; height: calc(100vh - 120px); }
    .left, .right { display: flex; flex-direction: column; min-height: 0; }
    /* max-content rows: in the fixed-height scrolling panel, auto rows would shrink to min-height and clip the tiles. */
    .tiles { flex: 1; min-height: 0; overflow: auto; display: grid; gap: 6px; grid-template-columns: repeat(3, minmax(0, 1fr));
             grid-auto-rows: max-content; align-content: start; padding: 2px; }
    /* Compact tiles so a typical list fits without scrolling: name up to 2 lines, the rest one line each (full text in the tooltip). */
    .tile { min-height: 0; padding: 6px 8px; gap: 2px; }
    .tile .t-name { overflow-wrap: anywhere; line-height: 1.2; }
    .tile .ml, .tile .t-stock { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .tile .ml { line-height: 1.45; font-size: 12px; }
    .tile .t-price { font-size: 14px; }
    .ml { font-family: 'Nirmala UI', 'Noto Sans Malayalam', sans-serif; color: var(--muted); font-size: 12.5px; }
    .cart { flex: 1; min-height: 0; overflow: auto; display: flex; flex-direction: column; gap: 4px; }
    /* One compact row per offering so many fit without scrolling: what / devotee / star / qty / amount / remove. */
    .line { display: grid; grid-template-columns: minmax(0, 1.2fr) minmax(0, 0.9fr) minmax(150px, 1.1fr) 50px auto auto; gap: 6px; align-items: center;
            border: 1px solid var(--line); border-radius: var(--radius-lg); padding: 4px 8px; background: var(--surface); }
    .line.bad { border-color: var(--danger); }
    .line .what { min-width: 0; }
    .line .what b { display: block; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .line .amt { font-weight: 700; text-align: right; white-space: nowrap; }
    .who { display: grid; grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr) 140px; gap: 8px; }
    .pay { display: grid; grid-template-columns: 130px minmax(0, 1fr) minmax(0, 1fr) auto; gap: 8px; align-items: end; }
    .pay .wide { grid-column: span 2; }
    .pay .sum { text-align: right; padding-left: 8px; }
    .when { margin-top: 2px; font-size: 12px; display: flex; gap: 6px; align-items: center; min-width: 0; white-space: nowrap; }
    .when .badge { min-width: 0; flex-shrink: 1; overflow: hidden; text-overflow: ellipsis; }
    .when .link-btn { flex-shrink: 0; }
    .foot { border-top: 1px solid var(--line); padding-top: 8px; margin-top: 6px; display: flex; flex-direction: column; gap: 8px; }
    .total { font-size: 24px; font-weight: 800; color: var(--primary); line-height: 1.1; }
    .dates { max-height: 300px; overflow: auto; }
    @media (max-width: 1000px) {
      .counter { grid-template-columns: 1fr; height: auto; }
      .who { grid-template-columns: 1fr 1fr; } .who .lookup { grid-column: span 2; }
      .pay { grid-template-columns: 1fr 1fr; } .pay .sum { grid-column: span 2; }
      .line { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 50px auto auto; } .line .what { grid-column: 1 / -1; }
    }
  `],
  template: `
    <app-hotkeys [keys]="hotkeys" />
    <div class="page">
      <div class="page-header"><h1>Vazhipadu counter</h1>
        <div class="toolbar">@if (today(); as t) {<span class="badge info">{{ t.malayalamDate }} · {{ t.starName }} · {{ t.tithiName }}</span>}
          @if (auth.can('Settings')) {<a class="btn sm" routerLink="/settings/vazhipadu-printing">Print settings</a>}</div></div>
      <div class="counter">
        <div class="left card"><section class="stack" style="min-height:0;flex:1;display:flex">
          <div class="toolbar">
            <input [(ngModel)]="filter" placeholder="Search offering…" style="flex:1;min-width:160px" aria-label="Search offering" />
            <div class="tabs" style="margin:0">
              <button [class.active]="deityFilter() === null" (click)="deityFilter.set(null)">All</button>
              @for (d of deities(); track d.id) {<button [class.active]="deityFilter() === d.id" (click)="deityFilter.set(d.id)">{{ d.name }}</button>}
            </div>
          </div>
          <div class="tiles">
            @for (o of shown(); track o.id) {
              <button class="tile" (click)="add(o)" [title]="o.name + (o.regionalName ? ' · ' + o.regionalName : '') + (o.deityName ? ' · ' + o.deityName : '') + (o.poojaSlotName ? ' · ' + o.poojaSlotName : '')"><span class="t-name">{{ o.name }}</span>
                @if (o.regionalName) {<span class="ml">{{ o.regionalName }}</span>}
                <span class="t-stock">{{ o.deityName }}{{ o.poojaSlotName ? ' · ' + o.poojaSlotName : '' }}</span>
                <span class="t-price">₹{{ o.rate | money }}</span></button>
            } @empty { <div class="empty muted">No offerings. Add items and tick “Temple offering” in Inventory › Products.</div> }
          </div>
        </section></div>

        <div class="right card"><section class="stack" style="flex:1;min-height:0;display:flex">
          <div class="who">
            <label class="field lookup"><span>Devotee (name / phone)</span>
              <app-lookup [search]="searchDevotee" [display]="devoteeText" [sub]="devoteeSub" placeholder="Name or phone" [value]="contactName"
                          (picked)="pickDevotee($event)" (entered)="contactName = $event" /></label>
            <label class="field"><span>Name on receipt</span><input [(ngModel)]="contactName" /></label>
            <label class="field"><span>Phone</span><input [(ngModel)]="phone" inputmode="tel" /></label>
          </div>
          <div class="cart">
            @for (l of cart(); track l.key) {
              <div class="line" [class.bad]="!!l.error">
                <div class="what"><b [title]="l.offering.name">{{ l.offering.name }}</b>
                  <div class="when"><span class="badge" [class.info]="l.schedule.kind !== 'Once'" [title]="l.description">{{ l.description }}</span>
                    <button class="link-btn" (click)="openSchedule(l)">Change days</button>
                    @if (l.error) {<span class="danger-text">{{ l.error }}</span>}</div></div>
                <input [(ngModel)]="l.devoteeName" placeholder="Devotee name" aria-label="Devotee name" />
                <select [(ngModel)]="l.star" aria-label="Star"><option [ngValue]="null">{{ l.offering.needsStar ? 'Star…' : '(no star)' }}</option>
                  @for (x of names()?.stars ?? []; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select>
                <input type="number" min="1" class="num" [(ngModel)]="l.qty" aria-label="Quantity" />
                <span class="amt" [title]="'₹' + (l.offering.rate | money) + ' × ' + l.qty + ' × ' + l.count + ' day(s)'">₹{{ lineAmount(l) | money }}
                  @if (l.count > 1) {<div class="muted small">{{ l.count }} days</div>}</span>
                <button class="btn ghost icon sm" (click)="remove(l)" aria-label="Remove">✕</button>
              </div>
            } @empty { <div class="empty muted">Tap an offering to add it.</div> }
          </div>
          <div class="foot">
            <div class="pay">
              <label class="field"><span>Paid by</span><select [(ngModel)]="payMode"><option value="Cash">Cash</option><option value="Card">Card / UPI</option></select></label>
              @if (payMode === 'Cash') {
                <label class="field"><span>Received</span><input type="number" class="num" [(ngModel)]="tendered" /></label>
                <label class="field"><span>Balance</span><input class="num" [value]="balance()" readonly /></label>
              } @else {
                <label class="field wide"><span>UPI / card ref.</span><input [(ngModel)]="payRef" /></label>
              }
              <div class="sum"><div class="muted small">Total</div><div class="total">₹{{ total() | money }}</div></div>
            </div>
            <div class="row center-y">
              <label class="check grow"><input type="checkbox" [(ngModel)]="saveDevotee" /> Remember devotee</label>
              <button class="btn" (click)="clear()">Clear</button>
              <button class="btn primary" [disabled]="saving() || !cart().length" (click)="book()">Book &amp; print (F8)</button>
            </div>
          </div>
        </section></div>
      </div>
    </div>

    @if (editing(); as l) {
      <app-modal title="When to perform — {{ l.offering.name }}" size="lg" (closed)="closeSchedule()">
        <app-schedule-editor [s]="draft" (changed)="previewDraft()" />
        <div style="margin-top:12px">
          @if (draftPreview(); as p) {
            <div class="info-box"><b>{{ p.description }}</b> — {{ p.count }} day(s), ₹{{ (l.offering.rate * l.qty * p.count) | money }}</div>
            <div class="table-wrap dates" style="margin-top:8px"><table class="grid">
              <thead><tr><th>Date</th><th>Day</th><th>Malayalam date</th><th>Star</th><th>Tithi</th></tr></thead>
              <tbody>@for (d of p.dates; track d.date) {<tr><td>{{ d.date | dmy }}</td><td>{{ d.weekday }}</td><td>{{ d.malayalamDate }}</td><td>{{ d.star }}</td><td>{{ d.tithi }}</td></tr>}
                @empty {<tr><td colspan="5" class="empty">No days match in this period.</td></tr>}</tbody>
            </table></div>
          }
          @if (draftError()) {<div class="error-box">{{ draftError() }}</div>}
        </div>
        <button footer class="btn primary" [disabled]="!draftPreview()?.count" (click)="applySchedule(l)">Use these days</button>
      </app-modal>
    }
    @if (printAsk(); as p) {
      <app-print-options [bookingNo]="p.bookingNo" [lineCount]="p.lineCount" (print)="printNow(p, $event)" (cancelled)="printAsk.set(null)" />
    }`,
})
export class VazhipaduComponent implements OnInit {
  private api = inject(Api);
  private ui = inject(Ui);
  auth = inject(Auth);
  /** The booking waiting for the print confirmation (Temple › Vazhipadu Print Settings › ask before printing). */
  printAsk = signal<PrintAsk | null>(null);
  printNow(p: PrintAsk, choice: PrintChoice): void {
    this.printAsk.set(null);
    printBooking(this.api, this.auth, p.id, p.lineCount, choice).catch(e => this.ui.error(errorMessage(e)));
  }
  offerings = signal<Offering[]>([]);
  deities = signal<Deity[]>([]);
  names = signal<CalendarNames | null>(null);
  today = signal<CalendarDay | null>(null);
  deityFilter = signal<number | null>(null);
  filterText = signal('');
  get filter(): string { return this.filterText(); }
  set filter(v: string) { this.filterText.set(v); }
  cart = signal<CartLine[]>([]);
  editing = signal<CartLine | null>(null);
  draft: Schedule = newSchedule();
  draftPreview = signal<SchedulePreview | null>(null);
  draftError = signal<string | null>(null);
  saving = signal(false);
  devoteeId: number | null = null;
  contactName = '';
  phone = '';
  address: string | null = null;
  payMode: 'Cash' | 'Card' = 'Cash';
  payRef = '';
  tendered: number | null = null;
  saveDevotee = true;
  private seq = 0;
  private previewTimer: any;
  hotkeys: Record<string, (e: KeyboardEvent) => void> = { F8: () => this.book() };

  shown = computed(() => {
    const f = this.filterText().trim().toLowerCase();
    const d = this.deityFilter();
    return this.offerings().filter(o => (d === null || o.deityId === d)
      && (!f || o.name.toLowerCase().includes(f) || (o.regionalName ?? '').includes(f) || o.code.toLowerCase() === f));
  });
  total = computed(() => this.cart().reduce((s, l) => s + this.lineAmount(l), 0));

  async ngOnInit(): Promise<void> {
    try {
      const [o, d, n, t] = await Promise.all([
        this.api.get<Offering[]>('/temple/offerings'), this.api.get<Deity[]>('/temple/deities', { activeOnly: true }),
        calendarNames(this.api), this.api.get<CalendarDay>(`/calendar/day/${today()}`),
      ]);
      this.offerings.set(o); this.deities.set(d); this.names.set(n); this.today.set(t);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  lineAmount(l: CartLine): number { return Math.round(l.offering.rate * (l.qty || 0) * l.count * 100) / 100; }
  balance(): string { return this.tendered ? (this.tendered - this.total()).toFixed(2) : ''; }

  searchDevotee = async (term: string): Promise<Devotee[]> => (await this.api.get<Paged<Devotee>>('/temple/devotees', { search: term, pageSize: 10 })).items;
  devoteeText = (d: Devotee): string => d.name;
  devoteeSub = (d: Devotee): string => [d.starName, d.houseName, d.phone].filter(x => !!x).join(' · ');
  pickDevotee(d: Devotee): void {
    this.devoteeId = d.id; this.contactName = d.name; this.phone = d.phone ?? ''; this.address = d.address;
    this.cart.update(c => c.map(l => l.devoteeName ? l : { ...l, devoteeName: d.name, star: d.star }));
    this.lastName = d.name; this.lastStar = d.star;
  }
  private lastName = '';
  private lastStar: number | null = null;

  add(o: Offering): void {
    const prev = this.cart().at(-1);
    const line: CartLine = {
      key: ++this.seq, offering: o, devoteeName: prev?.devoteeName || this.lastName || this.contactName, star: prev ? prev.star : this.lastStar,
      qty: 1, remarks: '', schedule: newSchedule(), count: 1, description: 'Today', dates: [], error: null,
    };
    this.cart.update(c => [...c, line]);
  }
  remove(l: CartLine): void { this.cart.update(c => c.filter(x => x !== l)); }

  openSchedule(l: CartLine): void {
    this.draft = { ...l.schedule };
    this.draftPreview.set(null); this.draftError.set(null);
    this.editing.set(l);
    this.previewDraft();
  }
  closeSchedule(): void { this.editing.set(null); }
  previewDraft(): void {
    clearTimeout(this.previewTimer);
    this.previewTimer = setTimeout(async () => {
      const l = this.editing();
      if (!l) return;
      try {
        this.draftPreview.set(await this.api.post<SchedulePreview>('/vazhipadu/preview', { schedule: scheduleBody(this.draft), rate: l.offering.rate, qty: l.qty || 1 }));
        this.draftError.set(null);
      } catch (e) { this.draftPreview.set(null); this.draftError.set(errorMessage(e)); }
    }, 250);
  }
  applySchedule(l: CartLine): void {
    const p = this.draftPreview();
    if (!p) return;
    const s = { ...this.draft };
    this.cart.update(c => c.map(x => x === l ? { ...x, schedule: s, count: p.count, dates: p.dates, error: null,
      description: s.kind === 'Once' ? (s.start === today() ? 'Today' : p.description) : `${p.description} (${p.count})` } : x));
    this.editing.set(null);
  }

  clear(): void {
    this.cart.set([]); this.devoteeId = null; this.contactName = ''; this.phone = ''; this.address = null; this.tendered = null; this.payRef = '';
    this.lastName = ''; this.lastStar = null;
  }

  async book(): Promise<void> {
    const lines = this.cart();
    if (!lines.length || this.saving()) return;
    let bad = false;
    const checked = lines.map(l => {
      let error: string | null = null;
      if (l.offering.needsName && !l.devoteeName.trim()) error = 'Enter the devotee name.';
      else if (l.offering.needsStar && l.star === null) error = 'Choose the star.';
      else if (!(l.qty > 0)) error = 'Enter the quantity.';
      bad ||= !!error;
      return { ...l, error };
    });
    this.cart.set(checked);
    if (bad) { this.ui.error('Some offerings need details (marked in red).'); return; }
    const contact = this.contactName.trim() || checked[0].devoteeName.trim();
    this.saving.set(true);
    try {
      const b = await this.api.post<{ id: number; bookingNo: string; total: number }>('/vazhipadu/bookings', {
        devoteeId: this.devoteeId, contactName: contact, phone: this.phone || null, address: this.address, paymentMode: this.payMode,
        paymentReference: this.payRef || null, tendered: this.payMode === 'Cash' ? this.tendered : null, saveDevotee: this.saveDevotee,
        lines: checked.map(l => ({
          productId: l.offering.id, deityId: l.offering.deityId, poojaSlotId: l.offering.poojaSlotId, devoteeName: l.devoteeName.trim() || contact,
          star: l.star, qty: l.qty, schedule: scheduleBody(l.schedule), remarks: l.remarks || null,
        })),
      });
      this.ui.ok(`Booked — receipt ${b.bookingNo}, ₹${b.total.toFixed(2)}`);
      this.clear();
      const ask = { id: b.id, bookingNo: b.bookingNo, lineCount: checked.length };
      if (this.auth.flag('VazhipaduConfirmPrint')) this.printAsk.set(ask);
      else this.printNow(ask, defaultPrintChoice(this.auth));
    } catch (e) { this.ui.error(errorMessage(e)); }
    finally { this.saving.set(false); }
  }
}

/* ================= Bookings ================= */

interface BookingListItem { id: number; bookingNo: string; bookedAt: string; contactName: string; phone: string | null; total: number; status: string; offerings: string; occurrences: number; done: number; nextDate: string | null; }
interface BookingLine {
  id: number; lineNo: number; offering: string; offeringMl: string | null; deity: string | null; poojaSlot: string | null; devoteeName: string;
  starName: string | null; qty: number; rate: number; amount: number; schedule: string; occurrences: number; done: number; cancelled: number; nextDate: string | null; dates: string[];
}
interface Booking { id: number; bookingNo: string; bookedAt: string; contactName: string; phone: string | null; address: string | null; total: number; status: string; notes: string | null; userName: string | null; lines: BookingLine[]; }

@Component({
  selector: 'app-bookings',
  standalone: true,
  imports: [FormsModule, ModalComponent, PagerComponent, MoneyPipe, DmyPipe, DmyhmPipe, PrintOptionsComponent],
  template: `
    <div class="page">
      <div class="page-header"><h1>Vazhipadu bookings</h1></div>
      <div class="toolbar" style="margin-bottom:12px">
        <label class="field"><span>From</span><input type="date" [(ngModel)]="from" /></label>
        <label class="field"><span>To</span><input type="date" [(ngModel)]="to" /></label>
        <label class="field"><span>Search</span><input [(ngModel)]="search" placeholder="Receipt, name, phone" (keydown.enter)="load(1)" /></label>
        <label class="field"><span>Show</span><select [(ngModel)]="active"><option [ngValue]="null">All</option><option [ngValue]="true">Still running</option><option [ngValue]="false">Completed / cancelled</option></select></label>
        <button class="btn primary" (click)="load(1)" style="align-self:flex-end">Show</button>
      </div>
      <div class="table-wrap"><table class="grid">
        <thead><tr><th>Receipt</th><th>Booked</th><th>Name</th><th>Phone</th><th>Offerings</th><th class="num">Done</th><th>Next day</th><th class="num">Amount</th><th>Status</th></tr></thead>
        <tbody>
          @for (b of rows(); track b.id) {
            <tr class="clickable" [class.cancelled]="b.status === 'Cancelled'" (click)="open(b.id)"><td>{{ b.bookingNo }}</td><td>{{ b.bookedAt | dmyhm }}</td><td><b>{{ b.contactName }}</b></td><td>{{ b.phone }}</td>
              <td>{{ b.offerings }}</td><td class="num">{{ b.done }} / {{ b.occurrences }}</td><td>{{ b.nextDate | dmy }}</td><td class="num">{{ b.total | money }}</td>
              <td><span class="badge" [class.danger]="b.status === 'Cancelled'">{{ b.status }}</span></td></tr>
          } @empty { <tr><td colspan="9" class="empty">No bookings for these filters.</td></tr> }
        </tbody>
      </table></div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="50" (go)="load($event)" />
    </div>
    @if (detail(); as b) {
      <app-modal title="Receipt {{ b.bookingNo }}" size="lg" (closed)="detail.set(null)">
        <div class="muted" style="margin-bottom:8px">{{ b.contactName }}{{ b.phone ? ' · ' + b.phone : '' }} · booked {{ b.bookedAt | dmyhm }} by {{ b.userName }} · ₹{{ b.total | money }}
          @if (b.status === 'Cancelled') {<span class="badge danger">Cancelled</span>}</div>
        <div class="table-wrap"><table class="grid">
          <thead><tr><th>Offering</th><th>Devotee</th><th>Star</th><th>When</th><th class="num">Done</th><th>Next</th><th class="num">Amount</th><th></th></tr></thead>
          <tbody>@for (l of b.lines; track l.id) {
            <tr><td><b>{{ l.offering }}</b><div class="muted small">{{ l.deity }}{{ l.poojaSlot ? ' · ' + l.poojaSlot : '' }}</div></td><td>{{ l.devoteeName }}</td><td>{{ l.starName }}</td>
              <td>{{ l.schedule }}</td><td class="num">{{ l.done }} / {{ l.occurrences - l.cancelled }}</td><td>{{ l.nextDate | dmy }}</td><td class="num">{{ l.amount | money }}</td>
              <td>@if (l.nextDate && b.status !== 'Cancelled' && auth.can('Vazhipadu', 'Edit')) {<button class="btn sm" (click)="stop(b, l)">Stop…</button>}</td></tr>
            <tr><td colspan="8" class="muted small">@for (d of l.dates; track d) {<span style="margin-right:8px">{{ d | dmy }}</span>}</td></tr>
          }</tbody>
        </table></div>
        @if (b.status !== 'Cancelled' && auth.can('Vazhipadu', 'Cancel')) {<button footer class="btn danger" (click)="cancel(b)">Cancel booking</button>}
        @if (b.status === 'Cancelled') {<button footer class="btn" (click)="print(b, { slips: false, duplicates: 0 })">Print receipt</button>}
        @else {<button footer class="btn" (click)="printAsk.set({ id: b.id, bookingNo: b.bookingNo, lineCount: b.lines.length })">Print…</button>}
      </app-modal>
    }
    @if (printAsk(); as p) {
      <app-print-options [bookingNo]="p.bookingNo" [lineCount]="p.lineCount" (print)="printChosen(p, $event)" (cancelled)="printAsk.set(null)" />
    }`,
})
export class BookingsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  /** Reprint: the print confirmation (cut print, duplicates) is always shown. */
  printAsk = signal<PrintAsk | null>(null);
  printChosen(p: PrintAsk, choice: PrintChoice): void {
    this.printAsk.set(null);
    printBooking(this.api, this.auth, p.id, p.lineCount, choice).catch(e => this.ui.error(errorMessage(e)));
  }
  from = addDays(today(), -30);
  to = today();
  search = '';
  active: boolean | null = null;
  rows = signal<BookingListItem[]>([]);
  total = signal(0);
  page = signal(1);
  detail = signal<Booking | null>(null);
  ngOnInit(): void { this.load(1); }
  async load(page: number): Promise<void> {
    try {
      const r = await this.api.get<Paged<BookingListItem>>('/vazhipadu/bookings', { from: this.from, to: this.to, search: this.search, active: this.active, page });
      this.rows.set(r.items); this.total.set(r.total); this.page.set(r.page);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async open(id: number): Promise<void> { try { this.detail.set(await this.api.get<Booking>(`/vazhipadu/bookings/${id}`)); } catch (e) { this.ui.error(errorMessage(e)); } }
  print(b: Booking, choice: PrintChoice): void { printBooking(this.api, this.auth, b.id, b.lines.length, choice).catch(e => this.ui.error(errorMessage(e))); }
  async stop(b: Booking, l: BookingLine): Promise<void> {
    const from = await this.ui.prompt(`Stop ${l.offering} for ${l.devoteeName}`, 'Stop from (dd-mm-yyyy or yyyy-mm-dd)', { value: l.nextDate ?? today(), message: 'Days from this date on are cancelled; days already done stay.' });
    if (!from) return;
    const iso = /^\d{2}-\d{2}-\d{4}$/.test(from) ? `${from.substring(6)}-${from.substring(3, 5)}-${from.substring(0, 2)}` : from;
    try { const r = await this.api.post<{ stopped: number }>(`/vazhipadu/lines/${l.id}/stop`, { from: iso }); this.ui.ok(`${r.stopped} day(s) cancelled.`); await this.open(b.id); await this.load(this.page()); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async cancel(b: Booking): Promise<void> {
    const reason = await this.ui.prompt(`Cancel receipt ${b.bookingNo}`, 'Reason', { message: 'The bill is cancelled and all days are removed from the pooja list. Refund the devotee separately.', okText: 'Cancel booking' });
    if (reason === null) return;
    try { await this.api.post(`/vazhipadu/bookings/${b.id}/cancel`, { reason }); this.ui.ok('Booking cancelled.'); this.detail.set(null); await this.load(this.page()); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Pooja list ================= */

interface PoojaRow {
  occurrenceId: number; bookingId: number; bookingNo: string; offering: string; offeringMl: string | null; deity: string | null; poojaSlot: string | null;
  slotTime: string | null; devoteeName: string; star: number | null; starName: string | null; starNameMl: string | null; qty: number; status: string;
  remarks: string | null; phone: string | null; doneBy: string | null;
}
interface PoojaDay { date: string; calendar: CalendarDay; rows: PoojaRow[]; summary: { offering: string; offeringMl: string | null; qty: number; count: number }[]; }

@Component({
  selector: 'app-pooja-list',
  standalone: true,
  imports: [FormsModule, QtyPipe],
  styles: [`.ml { font-family: 'Nirmala UI', 'Noto Sans Malayalam', sans-serif; color: var(--muted); } tr.done td { color: var(--muted); } .slot-head td { background: var(--surface-3); font-weight: 700; }`],
  template: `
    <div class="page">
      <div class="page-header"><h1>Pooja list</h1>
        <div class="toolbar"><button class="btn" (click)="print()">Print</button></div></div>
      <div class="toolbar" style="margin-bottom:12px">
        <button class="btn" (click)="shift(-1)" aria-label="Previous day">‹</button>
        <input type="date" [(ngModel)]="date" (ngModelChange)="load()" aria-label="Date" />
        <button class="btn" (click)="shift(1)" aria-label="Next day">›</button>
        <select [(ngModel)]="deityId" (ngModelChange)="load()" aria-label="Deity"><option [ngValue]="null">All deities</option>@for (d of deities(); track d.id) {<option [ngValue]="d.id">{{ d.name }}</option>}</select>
        <select [(ngModel)]="slotId" (ngModelChange)="load()" aria-label="Pooja time"><option [ngValue]="null">All pooja times</option>@for (s of slots(); track s.id) {<option [ngValue]="s.id">{{ s.name }}</option>}</select>
        <label class="check"><input type="checkbox" [(ngModel)]="includeCancelled" (ngModelChange)="load()" /> Show cancelled</label>
      </div>
      @if (day(); as d) {
        <div class="info-box" style="margin-bottom:12px"><b>{{ d.calendar.weekday }} · {{ d.calendar.malayalamDate }}</b> ({{ d.calendar.monthMl }} {{ d.calendar.malayalamDay }})
          · Star <b>{{ d.calendar.starName }}</b> <span class="ml">{{ d.calendar.starNameMl }}</span> · {{ d.calendar.tithiName }}
          @for (h of d.calendar.highlights; track h) {<span class="badge info" style="margin-left:6px">{{ h }}</span>}</div>
        <div class="cols" style="grid-template-columns: minmax(0, 3fr) minmax(240px, 1fr)">
          <div>
            @if (auth.can('PoojaList', 'Edit')) {
              <div class="toolbar" style="margin-bottom:8px"><span class="muted">{{ selected().size }} selected</span>
                <button class="btn sm" (click)="selectAll()">Select all</button>
                <button class="btn sm primary" [disabled]="!selected().size" (click)="mark(true)">Mark done</button>
                <button class="btn sm" [disabled]="!selected().size" (click)="mark(false)">Mark not done</button></div>
            }
            <div class="table-wrap"><table class="grid">
              <thead><tr><th></th><th>Offering</th><th>Devotee</th><th>Star</th><th class="num">Qty</th><th>Deity</th><th>Receipt</th><th>Status</th></tr></thead>
              <tbody>
                @for (r of d.rows; track r.occurrenceId; let i = $index) {
                  @if (i === 0 || d.rows[i - 1].poojaSlot !== r.poojaSlot) {<tr class="slot-head"><td colspan="8">{{ r.poojaSlot || 'Any time' }}{{ r.slotTime ? ' · ' + r.slotTime.substring(0, 5) : '' }}</td></tr>}
                  <tr [class.done]="r.status === 'Done'" [class.cancelled]="r.status === 'Cancelled'">
                    <td>@if (r.status !== 'Cancelled') {<input type="checkbox" [checked]="selected().has(r.occurrenceId)" (change)="toggle(r.occurrenceId)" [attr.aria-label]="'Select ' + r.devoteeName" />}</td>
                    <td><b>{{ r.offering }}</b> <span class="ml">{{ r.offeringMl }}</span></td>
                    <td>{{ r.devoteeName }}</td><td>{{ r.starName }} <span class="ml">{{ r.starNameMl }}</span></td><td class="num">{{ r.qty | qty }}</td>
                    <td>{{ r.deity }}</td><td>{{ r.bookingNo }}</td>
                    <td><span class="badge" [class.ok]="r.status === 'Done'" [class.danger]="r.status === 'Cancelled'">{{ r.status === 'Scheduled' ? 'To do' : r.status }}</span></td></tr>
                } @empty { <tr><td colspan="8" class="empty">No offerings booked for this day.</td></tr> }
              </tbody>
            </table></div>
          </div>
          <div class="card"><header><h2>To prepare</h2></header>
            <table class="grid"><tbody>@for (s of d.summary; track s.offering) {<tr><td>{{ s.offering }} <div class="ml small">{{ s.offeringMl }}</div></td><td class="num"><b>{{ s.qty | qty }}</b></td></tr>}
              @empty {<tr><td class="empty">Nothing booked.</td></tr>}</tbody></table></div>
        </div>
      }
    </div>`,
})
export class PoojaListComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  date = today();
  deityId: number | null = null;
  slotId: number | null = null;
  includeCancelled = false;
  deities = signal<Deity[]>([]);
  slots = signal<PoojaSlot[]>([]);
  day = signal<PoojaDay | null>(null);
  selected = signal(new Set<number>());
  async ngOnInit(): Promise<void> {
    try {
      const [d, s] = await Promise.all([this.api.get<Deity[]>('/temple/deities', { activeOnly: true }), this.api.get<PoojaSlot[]>('/temple/pooja-slots', { activeOnly: true })]);
      this.deities.set(d); this.slots.set(s);
    } catch (e) { this.ui.error(errorMessage(e)); }
    await this.load();
  }
  shift(days: number): void { this.date = addDays(this.date, days); this.load(); }
  async load(): Promise<void> {
    if (!this.date) return;
    try {
      this.day.set(await this.api.get<PoojaDay>(`/vazhipadu/pooja-list/${this.date}`, { deityId: this.deityId, slotId: this.slotId, includeCancelled: this.includeCancelled }));
      this.selected.set(new Set());
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  toggle(id: number): void { const s = new Set(this.selected()); s.has(id) ? s.delete(id) : s.add(id); this.selected.set(s); }
  selectAll(): void { this.selected.set(new Set((this.day()?.rows ?? []).filter(r => r.status !== 'Cancelled').map(r => r.occurrenceId))); }
  async mark(done: boolean): Promise<void> {
    try { const r = await this.api.post<{ updated: number }>('/vazhipadu/pooja-list/mark', { occurrenceIds: [...this.selected()], done }); this.ui.ok(`${r.updated} updated.`); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  print(): void { this.api.openPdf(`/vazhipadu/pooja-list/${this.date}/pdf`, undefined, { deityId: this.deityId, slotId: this.slotId }).catch(e => this.ui.error(errorMessage(e))); }
}

/* ================= Malayalam calendar ================= */

@Component({
  selector: 'app-temple-calendar',
  standalone: true,
  imports: [FormsModule, DmyPipe],
  styles: [`
    .month { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); gap: 4px; }
    .wd { font-weight: 700; font-size: 12px; color: var(--muted); text-align: center; padding: 4px; }
    .cell { min-height: 92px; border: 1px solid var(--line); border-radius: var(--radius); background: var(--surface); padding: 6px; cursor: pointer; text-align: left; font: inherit; color: inherit; display: flex; flex-direction: column; gap: 2px; }
    .cell:hover { border-color: var(--primary); }
    .cell.today { box-shadow: inset 0 0 0 2px var(--primary); }
    .cell.sel { background: var(--primary-soft); }
    .cell .d { font-weight: 800; font-size: 16px; display: flex; justify-content: space-between; }
    .cell .m { font-size: 11.5px; color: var(--primary); font-weight: 650; }
    .cell .s { font-size: 12px; }
    .cell .h { font-size: 11px; color: var(--warn); font-weight: 650; }
    .ml { font-family: 'Nirmala UI', 'Noto Sans Malayalam', sans-serif; }
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 320px; gap: 14px; }
    @media (max-width: 1000px) { .layout { grid-template-columns: 1fr; } }
  `],
  template: `
    <div class="page">
      <div class="page-header"><h1>Malayalam calendar</h1>
        <div class="toolbar"><button class="btn" (click)="move(-1)" aria-label="Previous month">‹</button><b style="min-width:150px;text-align:center">{{ title() }}</b>
          <button class="btn" (click)="move(1)" aria-label="Next month">›</button><button class="btn" (click)="goToday()">Today</button></div></div>
      <div class="layout">
        <div>
          <div class="month">
            @for (w of weekdays; track w) {<div class="wd">{{ w }}</div>}
            @for (b of blanks(); track $index) {<div></div>}
            @for (d of days(); track d.date) {
              <button class="cell" [class.today]="d.date === todayIso" [class.sel]="d.date === selected()?.date" (click)="select(d)">
                <span class="d">{{ +d.date.substring(8) }}<span class="m">{{ d.month }} {{ d.malayalamDay }}</span></span>
                <span class="s">{{ d.starName }}{{ d.extraStarName ? ' / ' + d.extraStarName : '' }}</span>
                @for (h of d.highlights; track h) {<span class="h">{{ h }}</span>}
                @if (d.corrected) {<span class="muted small">✎ corrected</span>}
              </button>
            }
          </div>
          <div class="card" style="margin-top:14px"><header><h2>Find star days</h2></header><section class="stack">
            <div class="toolbar"><select [(ngModel)]="findStar" aria-label="Star">@for (x of names()?.stars ?? []; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select>
              <button class="btn" (click)="findStarDays()">Next 12</button></div>
            @if (starDays().length) {<div class="toolbar">@for (d of starDays(); track d.date) {<span class="badge info">{{ d.date | dmy }} · {{ d.malayalamDate }}</span>}</div>}
          </section></div>
        </div>
        @if (selected(); as d) {
          <div class="card"><header><h2>{{ d.date | dmy }}</h2></header><section class="stack">
            <div><b>{{ d.weekday }}</b> <span class="ml muted">{{ d.weekdayMl }}</span></div>
            <div>{{ d.malayalamDate }} <div class="ml muted">{{ d.monthMl }} {{ d.malayalamDay }}</div></div>
            <div>Star: <b>{{ d.starName }}</b> <span class="ml">{{ d.starNameMl }}</span>
              <div class="muted small">At sunrise {{ d.starAtSunriseName }}, until {{ d.starAtSunriseEnds }}</div></div>
            <div>Tithi: <b>{{ d.tithiName }}</b> <span class="ml">{{ d.tithiNameMl }}</span><div class="muted small">until {{ d.tithiEnds }}</div></div>
            <div class="muted small">Sunrise {{ d.sunrise }} · Sunset {{ d.sunset }}{{ d.sankranti ? ' · Sankranti ' + d.sankranti : '' }}</div>
            @if (auth.can('TempleCalendar', 'Edit') && names(); as n) {
              <hr />
              <b>Correct this day</b>
              <div class="muted small">Use when the temple's panchangam differs. Leave a field empty to keep the calculated value.</div>
              <label class="field"><span>Star</span><select [(ngModel)]="fix.star"><option [ngValue]="null">As calculated</option>@for (x of n.stars; track x.id) {<option [ngValue]="x.id">{{ x.en }}</option>}</select></label>
              <div class="form-grid cols-2">
                <label class="field"><span>Month</span><select [(ngModel)]="fix.monthSign"><option [ngValue]="null">As calculated</option>@for (x of n.months; track x.id) {<option [ngValue]="x.id">{{ x.en }}</option>}</select></label>
                <label class="field"><span>Date</span><input type="number" min="1" max="32" class="num" [(ngModel)]="fix.malayalamDay" /></label>
              </div>
              <label class="field"><span>Tithi (1–30)</span><input type="number" min="1" max="30" class="num" [(ngModel)]="fix.tithi" /></label>
              <label class="field"><span>Festival / note</span><input [(ngModel)]="fix.festival" placeholder="e.g. Utsavam kodiyettam" /></label>
              <div class="toolbar"><button class="btn primary" (click)="saveFix(d)">Save</button>@if (d.corrected) {<button class="btn" (click)="clearFix(d)">Remove correction</button>}</div>
            }
          </section></div>
        }
      </div>
    </div>`,
})
export class TempleCalendarComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  weekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  todayIso = today();
  year = signal(+this.todayIso.substring(0, 4));
  month = signal(+this.todayIso.substring(5, 7));
  days = signal<CalendarDay[]>([]);
  selected = signal<CalendarDay | null>(null);
  names = signal<CalendarNames | null>(null);
  starDays = signal<CalendarDay[]>([]);
  findStar = 2;
  fix: { star: number | null; monthSign: number | null; malayalamDay: number | null; tithi: number | null; festival: string } = { star: null, monthSign: null, malayalamDay: null, tithi: null, festival: '' };
  title = computed(() => new Date(this.year(), this.month() - 1, 1).toLocaleDateString('en-IN', { month: 'long', year: 'numeric' }));
  blanks = computed(() => Array(new Date(this.year(), this.month() - 1, 1).getDay()).fill(0));
  async ngOnInit(): Promise<void> {
    calendarNames(this.api).then(n => this.names.set(n)).catch(() => {});
    await this.load(this.todayIso);
  }
  async load(select?: string): Promise<void> {
    const y = this.year(), m = this.month();
    const from = `${y}-${String(m).padStart(2, '0')}-01`;
    const to = addDays(`${m === 12 ? y + 1 : y}-${String(m === 12 ? 1 : m + 1).padStart(2, '0')}-01`, -1);
    try {
      const days = await this.api.get<CalendarDay[]>('/calendar', { from, to });
      this.days.set(days);
      const pick = days.find(d => d.date === (select ?? this.selected()?.date)) ?? null;
      if (pick) this.select(pick); else this.selected.set(null);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  move(delta: number): void {
    let m = this.month() + delta, y = this.year();
    if (m < 1) { m = 12; y--; } else if (m > 12) { m = 1; y++; }
    this.month.set(m); this.year.set(y); this.load();
  }
  goToday(): void { this.year.set(+this.todayIso.substring(0, 4)); this.month.set(+this.todayIso.substring(5, 7)); this.load(this.todayIso); }
  select(d: CalendarDay): void {
    this.selected.set(d);
    // A corrected day starts from its current values so saving a note keeps the earlier correction.
    this.fix = d.corrected
      ? { star: d.star, monthSign: d.monthSign, malayalamDay: d.malayalamDay, tithi: d.tithi, festival: d.festival ?? '' }
      : { star: null, monthSign: null, malayalamDay: null, tithi: null, festival: d.festival ?? '' };
  }
  async saveFix(d: CalendarDay): Promise<void> {
    try {
      await this.api.put('/calendar/corrections', { date: d.date, ...this.fix, festival: this.fix.festival.trim() || null });
      this.ui.ok('Saved.'); await this.load(d.date);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  async clearFix(d: CalendarDay): Promise<void> {
    try { await this.api.put('/calendar/corrections', { date: d.date }); this.ui.ok('Correction removed.'); await this.load(d.date); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async findStarDays(): Promise<void> {
    try { this.starDays.set(await this.api.get<CalendarDay[]>(`/calendar/stars/${this.findStar}`, { from: this.todayIso, count: 12 })); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Devotees ================= */

@Component({
  selector: 'app-devotees',
  standalone: true,
  imports: [FormsModule, ModalComponent, PagerComponent, ManglishDirective],
  template: `
    <div class="page">
      <div class="page-header"><h1>Devotees</h1>@if (auth.can('Devotee', 'Add')) {<button class="btn primary" (click)="edit(null)">New devotee</button>}</div>
      <div class="toolbar" style="margin-bottom:12px">
        <input [(ngModel)]="search" placeholder="Name, house, phone" (keydown.enter)="load(1)" aria-label="Search" />
        <select [(ngModel)]="star" (ngModelChange)="load(1)" aria-label="Star"><option [ngValue]="null">All stars</option>@for (x of names()?.stars ?? []; track x.id) {<option [ngValue]="x.id">{{ x.en }}</option>}</select>
        <button class="btn" (click)="load(1)">Search</button>
      </div>
      <div class="table-wrap"><table class="grid">
        <thead><tr><th>Name</th><th>Star</th><th>House name</th><th>Phone</th><th>City</th></tr></thead>
        <tbody>@for (d of rows(); track d.id) {
          <tr class="clickable" [class.cancelled]="!d.isActive" (click)="edit(d)"><td><b>{{ d.name }}</b></td><td>{{ d.starName }}</td><td>{{ d.houseName }}</td><td>{{ d.phone }}</td><td>{{ d.city }}</td></tr>
        } @empty {<tr><td colspan="5" class="empty">No devotees found. Devotees are also remembered when booking at the counter.</td></tr>}</tbody>
      </table></div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="50" (go)="load($event)" />
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit devotee' : 'New devotee'" (closed)="form.set(null)">
        <div class="form-grid cols-2">
          <label class="field req"><span>Name</span><input [(ngModel)]="f.name" /></label>
          <label class="field"><span>Name in Malayalam</span><input appManglish [(ngModel)]="f.regionalName" /></label>
          <label class="field"><span>Star</span><select [(ngModel)]="f.star"><option [ngValue]="null">—</option>@for (x of names()?.stars ?? []; track x.id) {<option [ngValue]="x.id">{{ x.en }} · {{ x.ml }}</option>}</select></label>
          <label class="field"><span>House name</span><input [(ngModel)]="f.houseName" /></label>
          <label class="field"><span>Phone</span><input [(ngModel)]="f.phone" /></label>
          <label class="field"><span>E-mail</span><input [(ngModel)]="f.email" /></label>
          <label class="field span-2"><span>Address</span><input [(ngModel)]="f.address" /></label>
          <label class="field"><span>City</span><input [(ngModel)]="f.city" /></label>
          <label class="field"><span>Date of birth</span><input type="date" [(ngModel)]="f.dateOfBirth" /></label>
          <label class="field span-2"><span>Notes</span><input [(ngModel)]="f.notes" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="f.isActive" /> Active</label>
        </div>
        <button footer class="btn primary" (click)="save(f)">Save</button>
      </app-modal>
    }`,
})
export class DevoteesComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  names = signal<CalendarNames | null>(null);
  rows = signal<Devotee[]>([]);
  total = signal(0);
  page = signal(1);
  form = signal<Devotee | null>(null);
  search = '';
  star: number | null = null;
  ngOnInit(): void { calendarNames(this.api).then(n => this.names.set(n)).catch(() => {}); this.load(1); }
  async load(page: number): Promise<void> {
    try {
      const r = await this.api.get<Paged<Devotee>>('/temple/devotees', { search: this.search, star: this.star, page });
      this.rows.set(r.items); this.total.set(r.total); this.page.set(r.page);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  edit(d: Devotee | null): void {
    this.form.set(d ? { ...d } : { id: 0, name: '', regionalName: null, star: null, starName: null, houseName: null, phone: null, address: null, city: null, email: null, dateOfBirth: null, notes: null, isActive: true });
  }
  async save(f: Devotee): Promise<void> {
    const body = { ...f, dateOfBirth: f.dateOfBirth || null };
    try { f.id ? await this.api.put(`/temple/devotees/${f.id}`, body) : await this.api.post('/temple/devotees', body); this.form.set(null); this.ui.ok('Saved.'); await this.load(this.page()); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Donations ================= */

interface Donation {
  id: number; receiptNo: string; date: string; donorName: string; houseName: string | null; phone: string | null; starName: string | null;
  purpose: string; amount: number; receivedInto: string; reference: string | null; pan: string | null; cancelled: boolean; userName: string | null;
}

@Component({
  selector: 'app-donations',
  standalone: true,
  imports: [FormsModule, PagerComponent, MoneyPipe, DmyhmPipe],
  template: `
    <div class="page">
      <div class="page-header"><h1>Donations</h1></div>
      @if (auth.can('Donation', 'Add')) {
        <div class="card" style="margin-bottom:16px"><header><h2>New donation receipt</h2></header><section>
          <div class="form-grid cols-4">
            <label class="field req"><span>Donor name</span><input [(ngModel)]="f.donorName" /></label>
            <label class="field"><span>House name</span><input [(ngModel)]="f.houseName" /></label>
            <label class="field"><span>Phone</span><input [(ngModel)]="f.phone" /></label>
            <label class="field"><span>Star</span><select [(ngModel)]="f.star"><option [ngValue]="null">—</option>@for (x of names()?.stars ?? []; track x.id) {<option [ngValue]="x.id">{{ x.en }}</option>}</select></label>
            <label class="field req"><span>For</span><select [(ngModel)]="f.purposeLedgerId">@for (p of purposes(); track p.id) {<option [ngValue]="p.id">{{ p.name }}</option>}</select></label>
            <label class="field req"><span>Amount</span><input type="number" class="num" [(ngModel)]="f.amount" /></label>
            <label class="field"><span>Received</span><select [(ngModel)]="f.mode"><option value="Cash">Cash</option><option value="Bank">Bank / UPI / cheque</option></select></label>
            <label class="field"><span>Reference</span><input [(ngModel)]="f.reference" placeholder="UPI / cheque no." /></label>
            <label class="field"><span>PAN (for 80G)</span><input [(ngModel)]="f.pan" maxlength="10" /></label>
            <label class="field span-2"><span>Address</span><input [(ngModel)]="f.address" /></label>
            <label class="field"><span>Notes</span><input [(ngModel)]="f.notes" /></label>
          </div>
          <div class="toolbar" style="margin-top:10px;justify-content:flex-end"><button class="btn primary" [disabled]="saving()" (click)="save()">Save &amp; print</button></div>
          @if (!purposes().length) {<div class="warn-box" style="margin-top:8px">Add donation heads as ledgers in the “Donations” account group.</div>}
        </section></div>
      }
      <div class="toolbar" style="margin-bottom:12px">
        <input [(ngModel)]="search" placeholder="Receipt, donor, phone" (keydown.enter)="load(1)" aria-label="Search" /><button class="btn" (click)="load(1)">Search</button></div>
      <div class="table-wrap"><table class="grid">
        <thead><tr><th>Receipt</th><th>Date</th><th>Donor</th><th>For</th><th>Into</th><th class="num">Amount</th><th></th></tr></thead>
        <tbody>@for (d of rows(); track d.id) {
          <tr [class.cancelled]="d.cancelled"><td>{{ d.receiptNo }}</td><td>{{ d.date | dmyhm }}</td><td><b>{{ d.donorName }}</b><div class="muted small">{{ d.houseName }} {{ d.phone }}</div></td>
            <td>{{ d.purpose }}</td><td>{{ d.receivedInto }}</td><td class="num">{{ d.amount | money }}</td>
            <td class="nowrap"><button class="btn sm" (click)="print(d)">Receipt</button>
              @if (!d.cancelled && auth.can('Donation', 'Cancel')) {<button class="btn sm danger" (click)="cancel(d)">Cancel</button>}
              @if (d.cancelled) {<span class="badge danger">Cancelled</span>}</td></tr>
        } @empty {<tr><td colspan="7" class="empty">No donations yet.</td></tr>}</tbody>
      </table></div>
      <app-pager [total]="total()" [page]="page()" [pageSize]="50" (go)="load($event)" />
    </div>`,
})
export class DonationsComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  names = signal<CalendarNames | null>(null);
  purposes = signal<{ id: number; name: string }[]>([]);
  rows = signal<Donation[]>([]);
  total = signal(0);
  page = signal(1);
  saving = signal(false);
  search = '';
  f = this.blank();
  blank() {
    return { donorName: '', houseName: '', phone: '', address: '', star: null as number | null, purposeLedgerId: null as number | null, amount: null as number | null,
      mode: 'Cash', reference: '', pan: '', notes: '' };
  }
  async ngOnInit(): Promise<void> {
    calendarNames(this.api).then(n => this.names.set(n)).catch(() => {});
    try {
      const p = await this.api.get<{ id: number; name: string }[]>('/donations/purposes');
      this.purposes.set(p);
      this.f.purposeLedgerId = p[0]?.id ?? null;
    } catch (e) { this.ui.error(errorMessage(e)); }
    await this.load(1);
  }
  async load(page: number): Promise<void> {
    try { const r = await this.api.get<Paged<Donation>>('/donations', { search: this.search, page }); this.rows.set(r.items); this.total.set(r.total); this.page.set(r.page); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async save(): Promise<void> {
    this.saving.set(true);
    try {
      const f = this.f;
      const d = await this.api.post<Donation>('/donations', { ...f, houseName: f.houseName || null, phone: f.phone || null, address: f.address || null,
        reference: f.reference || null, pan: f.pan || null, notes: f.notes || null });
      this.ui.ok(`Receipt ${d.receiptNo} saved.`);
      const purpose = f.purposeLedgerId;
      this.f = { ...this.blank(), purposeLedgerId: purpose };
      this.api.openPdf(`/donations/${d.id}/receipt`).catch(e => this.ui.error(errorMessage(e)));
      await this.load(1);
    } catch (e) { this.ui.error(errorMessage(e)); }
    finally { this.saving.set(false); }
  }
  print(d: Donation): void { this.api.openPdf(`/donations/${d.id}/receipt`).catch(e => this.ui.error(errorMessage(e))); }
  async cancel(d: Donation): Promise<void> {
    if (!(await this.ui.confirm('Cancel donation', `Cancel receipt ${d.receiptNo} of ₹${d.amount.toFixed(2)} from ${d.donorName}?`, { danger: true, okText: 'Cancel receipt' }))) return;
    try { await this.api.post(`/donations/${d.id}/cancel`); this.ui.ok('Cancelled.'); await this.load(this.page()); } catch (e) { this.ui.error(errorMessage(e)); }
  }
}

/* ================= Deities & pooja times ================= */

@Component({
  selector: 'app-temple-masters',
  standalone: true,
  imports: [FormsModule, ModalComponent, ManglishDirective],
  template: `
    <div class="page">
      <div class="page-header"><h1>Deities &amp; pooja times</h1></div>
      <div class="cols">
        <div class="card"><header><h2>Deities (prathishta)</h2>@if (auth.can('TempleMasters', 'Add')) {<button class="btn sm primary" (click)="editDeity(null)">Add</button>}</header>
          <table class="grid"><thead><tr><th>#</th><th>Name</th><th>Malayalam</th></tr></thead><tbody>
            @for (d of deities(); track d.id) {<tr class="clickable" [class.cancelled]="!d.isActive" (click)="editDeity(d)"><td>{{ d.sortOrder }}</td><td><b>{{ d.name }}</b></td><td>{{ d.regionalName }}</td></tr>}
          </tbody></table></div>
        <div class="card"><header><h2>Pooja times</h2>@if (auth.can('TempleMasters', 'Add')) {<button class="btn sm primary" (click)="editSlot(null)">Add</button>}</header>
          <table class="grid"><thead><tr><th>Time</th><th>Name</th><th>Malayalam</th></tr></thead><tbody>
            @for (s of slots(); track s.id) {<tr class="clickable" [class.cancelled]="!s.isActive" (click)="editSlot(s)"><td>{{ s.time?.substring(0, 5) }}</td><td><b>{{ s.name }}</b></td><td>{{ s.regionalName }}</td></tr>}
          </tbody></table></div>
      </div>
      <div class="muted small" style="margin-top:12px">Offerings (Archana, Payasam…) are items: add them in Inventory › Products and tick “Temple offering”, then choose the deity and pooja time.</div>
    </div>
    @if (deity(); as d) {
      <app-modal [title]="d.id ? 'Edit deity' : 'New deity'" size="sm" (closed)="deity.set(null)">
        <div class="form-grid cols-2">
          <label class="field req span-2"><span>Name</span><input [(ngModel)]="d.name" /></label>
          <label class="field span-2"><span>Malayalam</span><input appManglish [(ngModel)]="d.regionalName" /></label>
          <label class="field"><span>Order</span><input type="number" class="num" [(ngModel)]="d.sortOrder" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="d.isActive" /> Active</label>
        </div>
        <button footer class="btn primary" (click)="saveDeity(d)">Save</button>
      </app-modal>
    }
    @if (slot(); as s) {
      <app-modal [title]="s.id ? 'Edit pooja time' : 'New pooja time'" size="sm" (closed)="slot.set(null)">
        <div class="form-grid cols-2">
          <label class="field req span-2"><span>Name</span><input [(ngModel)]="s.name" /></label>
          <label class="field span-2"><span>Malayalam</span><input appManglish [(ngModel)]="s.regionalName" /></label>
          <label class="field"><span>Time</span><input type="time" [(ngModel)]="s.time" /></label>
          <label class="field"><span>Order</span><input type="number" class="num" [(ngModel)]="s.sortOrder" /></label>
          <label class="check"><input type="checkbox" [(ngModel)]="s.isActive" /> Active</label>
        </div>
        <button footer class="btn primary" (click)="saveSlot(s)">Save</button>
      </app-modal>
    }`,
})
export class TempleMastersComponent implements OnInit {
  auth = inject(Auth);
  private api = inject(Api);
  private ui = inject(Ui);
  deities = signal<Deity[]>([]);
  slots = signal<PoojaSlot[]>([]);
  deity = signal<Deity | null>(null);
  slot = signal<PoojaSlot | null>(null);
  ngOnInit(): void { this.load(); }
  async load(): Promise<void> {
    try {
      const [d, s] = await Promise.all([this.api.get<Deity[]>('/temple/deities'), this.api.get<PoojaSlot[]>('/temple/pooja-slots')]);
      this.deities.set(d); this.slots.set(s);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
  editDeity(d: Deity | null): void { this.deity.set(d ? { ...d } : { id: 0, name: '', regionalName: null, sortOrder: this.deities().length + 1, isActive: true }); }
  editSlot(s: PoojaSlot | null): void { this.slot.set(s ? { ...s, time: s.time?.substring(0, 5) ?? null } : { id: 0, name: '', regionalName: null, time: null, sortOrder: this.slots().length + 1, isActive: true }); }
  async saveDeity(d: Deity): Promise<void> {
    try { d.id ? await this.api.put(`/temple/deities/${d.id}`, d) : await this.api.post('/temple/deities', d); this.deity.set(null); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
  async saveSlot(s: PoojaSlot): Promise<void> {
    const body = { ...s, time: s.time ? s.time.substring(0, 5) + ':00' : null };
    try { s.id ? await this.api.put(`/temple/pooja-slots/${s.id}`, body) : await this.api.post('/temple/pooja-slots', body); this.slot.set(null); this.ui.ok('Saved.'); await this.load(); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }
}
