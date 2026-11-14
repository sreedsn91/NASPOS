import { Component, Injectable, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { IconComponent } from '../shared/icon';

export interface Toast { id: number; kind: 'ok' | 'error' | 'info' | 'warn'; text: string; }

interface DialogBase { id: number; title: string; message?: string; items?: string[]; resolve: (v: any) => void; }
interface ConfirmDialog extends DialogBase { kind: 'confirm'; okText: string; danger: boolean; }
interface PromptDialog extends DialogBase { kind: 'prompt'; label: string; inputType: 'text' | 'password' | 'number'; value: string; okText: string; }
interface PrintDialog extends DialogBase { kind: 'print'; original: boolean; duplicate: boolean; }
type Dialog = ConfirmDialog | PromptDialog | PrintDialog;

/** Bill copies the print dialog can ask for (server copy marks). */
export type PrintCopy = 'original' | 'duplicate';

@Injectable({ providedIn: 'root' })
export class Ui {
  private seq = 0;
  readonly toasts = signal<Toast[]>([]);
  readonly dialogs = signal<Dialog[]>([]);
  readonly busy = signal(0);
  readonly isBusy = computed(() => this.busy() > 0);

  toast(text: string, kind: Toast['kind'] = 'ok', ms = 3500): void {
    const id = ++this.seq;
    this.toasts.update(t => [...t, { id, kind, text }]);
    setTimeout(() => this.toasts.update(t => t.filter(x => x.id !== id)), kind === 'error' ? Math.max(ms, 6000) : ms);
  }
  ok(text: string) { this.toast(text, 'ok'); }
  error(text: string) { this.toast(text, 'error'); }
  info(text: string) { this.toast(text, 'info'); }

  confirm(title: string, message?: string, opts: { items?: string[]; okText?: string; danger?: boolean } = {}): Promise<boolean> {
    return new Promise(resolve => {
      const d: ConfirmDialog = { id: ++this.seq, kind: 'confirm', title, message, items: opts.items, okText: opts.okText ?? 'OK', danger: !!opts.danger, resolve };
      this.dialogs.update(x => [...x, d]);
    });
  }

  prompt(title: string, label: string, opts: { type?: 'text' | 'password' | 'number'; value?: string; message?: string; okText?: string } = {}): Promise<string | null> {
    return new Promise(resolve => {
      const d: PromptDialog = { id: ++this.seq, kind: 'prompt', title, label, message: opts.message, inputType: opts.type ?? 'text', value: opts.value ?? '', okText: opts.okText ?? 'OK', resolve };
      this.dialogs.update(x => [...x, d]);
    });
  }

  /**
   * Print confirmation with copy choice. Resolves to the ticked copies (e.g. ['original', 'duplicate']), or null when skipped.
   * A first print defaults to the original; a reprint defaults to a marked duplicate.
   */
  printChoice(title: string, message?: string, opts: { reprint?: boolean } = {}): Promise<PrintCopy[] | null> {
    return new Promise(resolve => {
      const d: PrintDialog = { id: ++this.seq, kind: 'print', title, message, original: !opts.reprint, duplicate: !!opts.reprint, resolve };
      this.dialogs.update(x => [...x, d]);
    });
  }

  /** Copies ticked in a print dialog, in print order. */
  printCopies(d: PrintDialog): PrintCopy[] { return [...(d.original ? ['original' as const] : []), ...(d.duplicate ? ['duplicate' as const] : [])]; }

  close(d: Dialog, value: any): void {
    this.dialogs.update(x => x.filter(y => y.id !== d.id));
    d.resolve(value);
  }

  async run<T>(work: () => Promise<T>): Promise<T> {
    this.busy.update(b => b + 1);
    try { return await work(); } finally { this.busy.update(b => b - 1); }
  }
}

/** Renders toasts, confirm/prompt dialogs and the busy bar for the whole app. */
@Component({
  selector: 'app-ui-host',
  standalone: true,
  imports: [FormsModule, IconComponent],
  template: `
    @if (ui.isBusy()) { <div class="busy-bar"></div> }
    <div class="toasts">
      @for (t of ui.toasts(); track t.id) {
        <div [class]="'toast ' + t.kind" role="status">
          <app-icon class="t-icon" [name]="t.kind === 'ok' ? 'check' : t.kind === 'info' ? 'info' : 'alert'" [size]="18" />
          <span>{{ t.text }}</span>
        </div>
      }
    </div>
    @for (d of ui.dialogs(); track d.id) {
      <div class="modal-backdrop" (keydown.escape)="ui.close(d, d.kind === 'confirm' ? false : null)">
        <div class="modal sm" role="dialog" aria-modal="true">
          <header><h3>{{ d.title }}</h3></header>
          <section>
            @if (d.message) { <p class="pre">{{ d.message }}</p> }
            @if (d.items?.length) { <ul class="warn-list">@for (i of d.items; track $index) {<li>{{ i }}</li>}</ul> }
            @if (d.kind === 'print') {
              <div class="stack" style="gap:8px" (keydown.enter)="ui.printCopies(d).length && ui.close(d, ui.printCopies(d))">
                <label class="check"><input type="checkbox" [(ngModel)]="d.original" /> Original <span class="muted small">— marked "Original for recipient"</span></label>
                <label class="check"><input type="checkbox" [(ngModel)]="d.duplicate" /> Duplicate copy <span class="muted small">— marked "Duplicate copy"</span></label>
                @if (!d.original && !d.duplicate) { <small class="danger-text">Tick at least one copy.</small> }
              </div>
            }
            @if (d.kind === 'prompt') {
              <label class="field"><span>{{ d.label }}</span>
                <input #inp [type]="d.inputType" [(ngModel)]="d.value" (keydown.enter)="ui.close(d, d.value)" autofocus
                       [attr.autocomplete]="d.inputType === 'password' ? 'current-password' : 'off'" />
              </label>
            }
          </section>
          <footer>
            <button class="btn ghost" (click)="ui.close(d, d.kind === 'confirm' ? false : null)">{{ d.kind === 'print' ? 'Skip' : 'Cancel' }}</button>
            @if (d.kind === 'print') {
              <button class="btn primary" (click)="ui.close(d, ui.printCopies(d))" [disabled]="!d.original && !d.duplicate" autofocus>
                Print {{ d.original && d.duplicate ? '2 copies' : '' }}</button>
            } @else if (d.kind === 'confirm') {
              <button class="btn" [class.danger]="d.danger" [class.primary]="!d.danger" (click)="ui.close(d, true)" autofocus>{{ d.okText }}</button>
            } @else {
              <button class="btn primary" (click)="ui.close(d, d.value)">{{ d.okText }}</button>
            }
          </footer>
        </div>
      </div>
    }
  `,
})
export class UiHostComponent {
  constructor(public ui: Ui) {}
}
