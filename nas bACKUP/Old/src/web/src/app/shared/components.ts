import { Component, ElementRef, EventEmitter, HostListener, Input, Output, ViewChild, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ReportColumn, ReportResult } from '../core/models';
import { dmy, money, qty } from '../core/format';

@Component({
  selector: 'app-modal',
  standalone: true,
  template: `
    <div class="modal-backdrop" (mousedown.self)="dismissible && closed.emit()">
      <div class="modal" [class.sm]="size === 'sm'" [class.lg]="size === 'lg'" role="dialog" aria-modal="true" (keydown.escape)="closed.emit()">
        <header><h3>{{ title }}</h3><ng-content select="[header]"></ng-content>
          <button class="btn ghost icon" (click)="closed.emit()" aria-label="Close">✕</button></header>
        <section><ng-content></ng-content></section>
        <footer><ng-content select="[footer]"></ng-content></footer>
      </div>
    </div>`,
})
export class ModalComponent {
  @Input() title = '';
  @Input() size: 'sm' | 'md' | 'lg' = 'md';
  @Input() dismissible = true;
  @Output() closed = new EventEmitter<void>();
}

/**
 * Keyboard-first typeahead. Arrow keys move, Enter picks, Esc closes. The search function is debounced.
 */
@Component({
  selector: 'app-lookup',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="lookup">
      <input #box [placeholder]="placeholder" [(ngModel)]="text" (ngModelChange)="changed()" (keydown)="key($event)"
             (focus)="focused()" (blur)="blur()" [disabled]="disabled" autocomplete="off" [attr.aria-label]="placeholder" />
      @if (open() && items().length) {
        <div class="lookup-panel">
          @for (it of items(); track $index) {
            <div class="opt" [class.active]="$index === active()" (mousedown)="pick(it, $event)">
              <span class="main-t">{{ display(it) }}</span>
              @if (sub) { <span class="sub">{{ sub(it) }}</span> }
            </div>
          }
        </div>
      }
    </div>`,
})
export class LookupComponent<T = any> {
  @Input({ required: true }) search!: (term: string) => Promise<T[]>;
  @Input({ required: true }) display!: (item: T) => string;
  @Input() sub?: (item: T) => string;
  @Input() placeholder = 'Search…';
  @Input() disabled = false;
  @Input() minChars = 1;
  @Input() clearOnPick = false;
  @Input() searchOnFocus = false;
  @Input() set value(v: string | null | undefined) { this.text = v ?? ''; }
  @Output() picked = new EventEmitter<T>();
  /** Enter pressed with no dropdown (e.g. scanner input) → raw text. */
  @Output() entered = new EventEmitter<string>();
  @ViewChild('box', { static: true }) box!: ElementRef<HTMLInputElement>;

  text = '';
  items = signal<T[]>([]);
  open = signal(false);
  active = signal(0);
  private timer: any;
  private seq = 0;

  focus(): void { setTimeout(() => this.box.nativeElement.focus()); }

  focused(): void {
    if (this.searchOnFocus) this.run(this.text ?? '');
  }

  changed(): void {
    clearTimeout(this.timer);
    const t = (this.text ?? '').trim();
    if (t.length < this.minChars) { this.items.set([]); this.open.set(false); return; }
    this.timer = setTimeout(() => this.run(t), 180);
  }

  private async run(t: string): Promise<void> {
    const my = ++this.seq;
    try {
      const res = await this.search(t);
      if (my !== this.seq) return;
      this.items.set(res);
      this.active.set(0);
      this.open.set(true);
    } catch { /* errors surface via the caller */ }
  }

  async key(e: KeyboardEvent): Promise<void> {
    if (e.key === 'ArrowDown') { e.preventDefault(); this.active.set(Math.min(this.active() + 1, this.items().length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); this.active.set(Math.max(this.active() - 1, 0)); }
    else if (e.key === 'Escape') { this.open.set(false); }
    else if (e.key === 'Enter') {
      e.preventDefault();
      clearTimeout(this.timer);
      if (this.open() && this.items().length) { this.pick(this.items()[this.active()]); return; }
      const t = (this.text ?? '').trim();
      if (!t) return;
      this.entered.emit(t);
    }
  }

  pick(it: T, e?: Event): void {
    e?.preventDefault();
    this.picked.emit(it);
    this.text = this.clearOnPick ? '' : this.display(it);
    this.open.set(false);
    this.items.set([]);
  }

  blur(): void { setTimeout(() => this.open.set(false), 150); }
}

@Component({
  selector: 'app-pager',
  standalone: true,
  template: `
    <div class="pager">
      <span>{{ total() }} records</span>
      <span class="grow"></span>
      <button class="btn sm" [disabled]="page() <= 1" (click)="go.emit(page() - 1)">‹ Prev</button>
      <span>Page {{ page() }} / {{ pages() }}</span>
      <button class="btn sm" [disabled]="page() >= pages()" (click)="go.emit(page() + 1)">Next ›</button>
    </div>`,
})
export class PagerComponent {
  total = input(0);
  page = input(1);
  pageSize = input(50);
  go = output<number>();
  pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
}

export function formatCell(col: ReportColumn, v: unknown): string {
  if (v === null || v === undefined) return '';
  switch (col.type) {
    case 'money': return money(v);
    case 'qty': return qty(v);
    case 'number': return typeof v === 'number' ? qty(v) : String(v);
    case 'percent': return typeof v === 'number' ? qty(v) : String(v);
    case 'date': return dmy(v);
    default: return String(v);
  }
}

/** Renders any server ReportResult: typed columns, styled rows, totals, drill-down links. */
@Component({
  selector: 'app-report-grid',
  standalone: true,
  template: `
    @if (report(); as r) {
      <div class="table-wrap" [style.max-height]="maxHeight()">
        <table class="grid">
          <thead><tr>@for (c of r.columns; track c.key) {<th [class.num]="isNum(c)">{{ c.title }}</th>}</tr></thead>
          <tbody>
            @for (row of r.rows; track $index) {
              <tr [class]="rowClass(row)" [class.clickable]="!!row['_link']" (click)="row['_link'] && link.emit(row['_link'])">
                @for (c of r.columns; track c.key) {<td [class.num]="isNum(c)">{{ cell(c, row[c.key]) }}</td>}
              </tr>
            } @empty { <tr><td [attr.colspan]="r.columns.length" class="empty">No data for the selected filters.</td></tr> }
          </tbody>
          @if (r.totals) {
            <tfoot><tr>@for (c of r.columns; track c.key) {<td [class.num]="isNum(c)">{{ cell(c, r.totals[c.key]) }}</td>}</tr></tfoot>
          }
        </table>
      </div>
      @if (r.summary.length) {
        <div class="toolbar" style="margin-top:8px">@for (s of r.summary; track s.key) {<span class="badge info">{{ s.key }}: {{ s.value }}</span>}</div>
      }
    }`,
})
export class ReportGridComponent {
  report = input<ReportResult | null>(null);
  maxHeight = input('calc(100vh - 260px)');
  link = output<string>();
  isNum(c: ReportColumn): boolean { return c.type !== 'text' && c.type !== 'date'; }
  cell(c: ReportColumn, v: unknown): string { return formatCell(c, v); }
  rowClass(row: Record<string, any>): string {
    const s = row['_style'];
    return s === 'danger' ? 'danger' : s ? 'r-' + s : '';
  }
}

/** Global shortcut helper: components register key handlers while active. */
@Component({ selector: 'app-hotkeys', standalone: true, template: '' })
export class HotkeysComponent {
  @Input() keys: Record<string, (e: KeyboardEvent) => void> = {};
  @HostListener('window:keydown', ['$event'])
  onKey(e: KeyboardEvent): void {
    const id = (e.ctrlKey ? 'Ctrl+' : '') + (e.altKey ? 'Alt+' : '') + (e.shiftKey && e.key.length > 1 ? 'Shift+' : '') + e.key;
    const h = this.keys[id];
    if (h) { e.preventDefault(); h(e); }
  }
}
