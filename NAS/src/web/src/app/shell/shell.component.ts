import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { Api, errorMessage } from '../core/api';
import { Auth } from '../core/auth';
import { Live } from '../core/live';
import { CompanyDto, ReportDefinition } from '../core/models';
import { Theme } from '../core/theme';
import { Ui } from '../core/ui';
import { IconComponent } from '../shared/icon';
import { LogoComponent } from '../shared/logo';
import { MENU, MenuGroup } from './menu';

interface PaletteItem { label: string; group: string; link: string; icon: string; sub?: string; }

function initials(name: string | undefined): string {
  return (name ?? '?').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FormsModule, IconComponent, LogoComponent],
  template: `
    <div class="shell" [class.rail]="rail()" [class.peek]="peek()" [class.mobile-open]="mobileOpen()">
      <nav class="sidebar" aria-label="Main menu" (mouseenter)="hoverMenu(true)" (mouseleave)="hoverMenu(false)">
        <div class="brand">
          <app-logo class="brand-logo" [size]="34" />
          <div class="brand-text"><b>NAS POS Pro</b><small>GST billing &amp; accounts</small></div>
        </div>
        <button class="side-search" (click)="openPalette()" title="Search screens and reports (Ctrl+K)">
          <app-icon name="search" [size]="16" /><span>Search…</span><kbd>Ctrl K</kbd>
        </button>
        <div class="nav-tools">
          <span>Menu</span>
          <button (click)="toggleAll()" [title]="anyOpen() ? 'Collapse all menu groups' : 'Expand all menu groups'">{{ anyOpen() ? 'Collapse all' : 'Expand all' }}</button>
        </div>
        @for (g of menu(); track g.label) {
          <div class="nav-group">
            <button class="nav-head" [class.open]="isOpen(g.label)" (click)="toggleGroup(g.label)" [title]="g.label">
              <app-icon [name]="g.icon" [size]="18" /><span>{{ g.label }}</span>
              <app-icon class="chev" name="chevron" [size]="14" />
            </button>
            <div class="nav-items" [class.open]="isOpen(g.label)">
              <div>
                @for (m of g.items; track m.link) {
                  <a class="nav-link" [routerLink]="m.link" routerLinkActive="active">{{ m.label }}
                    @if (m.key) { <kbd>{{ m.key }}</kbd> }</a>
                }
              </div>
            </div>
          </div>
        }
        <div class="sidebar-foot">FY {{ auth.session()?.financialYear?.name }} · {{ auth.counter || 'no counter' }}</div>
      </nav>

      <div class="main">
        <header class="topbar">
          <button class="btn ghost icon" (click)="toggleRail()" aria-label="Toggle menu"><app-icon name="menu" /></button>
          @if (auth.session(); as s) {
            <div class="company-chip">
              <div class="avatar">{{ initials(s.company.name) }}</div>
              @if (companies().length > 1) {
                <select [ngModel]="s.company.id" (ngModelChange)="switchCompany($event)" aria-label="Company">
                  @for (c of companies(); track c.id) { <option [ngValue]="c.id">{{ c.name }}</option> }
                </select>
              } @else { <span class="c-name">{{ s.company.name }}</span> }
            </div>
            <select [ngModel]="s.financialYear.id" (ngModelChange)="switchYear($event)" aria-label="Financial year">
              @for (y of s.years; track y.id) { <option [ngValue]="y.id">FY {{ y.name }}{{ y.isClosed ? ' · closed' : '' }}</option> }
            </select>
            @if (s.financialYear.isClosed) { <span class="badge warn">Read-only year</span> }
            <span class="grow"></span>
            <button class="btn ghost sm" (click)="openPalette()" title="Search (Ctrl+K)"><app-icon name="search" [size]="16" /> <kbd>Ctrl K</kbd></button>
            <span class="status-dot" [class.on]="live.connected()" [title]="live.connected() ? 'Live updates connected' : 'Live updates offline'"><i></i>{{ live.connected() ? 'Live' : 'Offline' }}</span>
            <button class="user-btn" (click)="userMenu.set(!userMenu())" aria-haspopup="menu" [attr.aria-expanded]="userMenu()">
              <div class="avatar">{{ initials(s.user.displayName || s.user.userName) }}</div>
              <span>{{ s.user.displayName || s.user.userName }}</span>
            </button>
            @if (userMenu()) {
              <div class="dropdown" role="menu" (click)="$event.stopPropagation()">
                <div class="dd-head"><b>{{ s.user.displayName || s.user.userName }}</b>
                  <div class="muted small">{{ s.user.isAdmin ? 'Administrator' : 'User' }} · {{ s.company.name }}</div></div>
                <button (click)="setCounter()"><app-icon name="monitor" [size]="16" /> Counter: {{ auth.counter || 'not set' }}</button>
                <button (click)="toggleTheme()"><app-icon name="moon" [size]="16" /> {{ theme.isDark() ? 'Light mode' : 'Dark mode' }}</button>
                <button (click)="changePassword()"><app-icon name="key" [size]="16" /> Change password</button>
                <hr />
                <button class="danger" (click)="auth.logout()"><app-icon name="logout" [size]="16" /> Sign out</button>
              </div>
            }
          }
        </header>
        @if (auth.session()?.license; as l) {
          @if (l.state !== 'Active') {
            <div class="license-bar" [class.blocked]="!l.canBill" role="status">
              <app-icon name="alert" [size]="16" /><span>{{ l.message }}</span>
              <a routerLink="/settings/license">{{ l.canBill ? 'License' : 'Enter key' }}</a>
            </div>
          }
        }
        <main class="content" (click)="userMenu.set(false)"><router-outlet /></main>
      </div>
    </div>

    @if (palette()) {
      <div class="modal-backdrop" (mousedown.self)="palette.set(false)">
        <div class="modal palette" role="dialog" aria-label="Search screens and reports">
          <div class="p-search"><app-icon name="search" />
            <input #pbox [(ngModel)]="pq" (ngModelChange)="pActive.set(0)" (keydown)="paletteKey($event)" placeholder="Go to a screen or report…" autocomplete="off" /></div>
          <div class="p-list">
            @for (it of pResults(); track it.link + it.label; let i = $index) {
              @if (i === 0 || pResults()[i - 1].group !== it.group) { <div class="p-group">{{ it.group }}</div> }
              <div class="p-item" [class.active]="i === pActive()" (mouseenter)="pActive.set(i)" (mousedown)="go(it)">
                <app-icon [name]="it.icon" [size]="16" /><span>{{ it.label }}</span>@if (it.sub) { <span class="p-sub">{{ it.sub }}</span> }
              </div>
            } @empty { <div class="empty">Nothing matches “{{ pq }}”.</div> }
          </div>
          <div class="p-foot"><span><kbd>↑</kbd> <kbd>↓</kbd> move</span><span><kbd>Enter</kbd> open</span><span><kbd>Esc</kbd> close</span></div>
        </div>
      </div>
    }
  `,
})
export class ShellComponent implements OnInit, OnDestroy {
  auth = inject(Auth);
  live = inject(Live);
  private api = inject(Api);
  private ui = inject(Ui);
  private router = inject(Router);
  initials = initials;
  @ViewChild('pbox') pbox?: ElementRef<HTMLInputElement>;

  rail = signal(this.load('naspos.rail', false));
  /** Rail temporarily expanded (as an overlay) while the pointer rests on it. */
  peek = signal(false);
  private peekTimer?: ReturnType<typeof setTimeout>;
  mobileOpen = signal(false);
  userMenu = signal(false);
  companies = signal<CompanyDto[]>([]);
  openGroups = signal(new Set<string>(this.load<string[]>('naspos.menu', ['Home', 'Sales'])));
  theme = inject(Theme);
  palette = signal(false);
  pq = '';
  pActive = signal(0);
  private reports = signal<ReportDefinition[]>([]);
  private pTick = signal(0);

  menu = computed<MenuGroup[]>(() => {
    this.auth.session();
    return MENU.map(g => ({ ...g, items: g.items.filter(i => !i.screen || this.auth.can(i.screen, i.right ?? 'Read')) })).filter(g => g.items.length);
  });

  private paletteItems = computed<PaletteItem[]>(() => [
    ...this.menu().flatMap(g => g.items.map(i => ({ label: i.label, group: g.label, link: i.link, icon: g.icon, sub: i.key }))),
    ...this.reports().map(r => ({ label: r.title, group: 'Reports · ' + r.group, link: '/reports/' + r.key, icon: 'chart' })),
  ]);

  pResults = computed(() => {
    this.pTick();
    const q = this.pq.trim().toLowerCase();
    const items = this.paletteItems();
    if (!q) return items.filter(i => !i.group.startsWith('Reports ·')).slice(0, 40);
    const words = q.split(/\s+/);
    return items.filter(i => words.every(w => (i.label + ' ' + i.group).toLowerCase().includes(w))).slice(0, 60);
  });

  async ngOnInit(): Promise<void> {
    this.live.start();
    this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(() => { this.mobileOpen.set(false); this.userMenu.set(false); clearTimeout(this.peekTimer); this.peek.set(false); });
    try { this.companies.set(await this.api.get<CompanyDto[]>('/company/mine')); } catch { /* ignore */ }
  }

  ngOnDestroy(): void { clearTimeout(this.peekTimer); this.live.stop(); }

  private load<T>(key: string, fallback: T): T {
    try { const v = localStorage.getItem(key); return v ? JSON.parse(v) : fallback; } catch { return fallback; }
  }
  private save(key: string, v: unknown): void { try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* ignore */ } }

  isOpen(label: string): boolean { return this.openGroups().has(label); }

  anyOpen = computed(() => this.menu().some(g => this.openGroups().has(g.label)));

  toggleAll(): void {
    const s = new Set(this.anyOpen() ? [] : this.menu().map(g => g.label));
    this.openGroups.set(s);
    this.save('naspos.menu', [...s]);
  }

  /** Short delays so a pointer passing over the rail doesn't flash it open, and a brief exit doesn't snap it shut. */
  hoverMenu(inside: boolean): void {
    clearTimeout(this.peekTimer);
    if (!this.rail() || window.innerWidth <= 820) { this.peek.set(false); return; }
    this.peekTimer = setTimeout(() => this.peek.set(inside), inside ? 150 : 250);
  }

  toggleGroup(label: string): void {
    // Clicking a group on the bare rail (e.g. touch, no hover) expands the menu for good; while peeking it just opens the group.
    if (this.rail() && !this.peek()) { this.rail.set(false); this.save('naspos.rail', false); }
    const s = new Set(this.openGroups());
    if (s.has(label)) s.delete(label); else s.add(label);
    this.openGroups.set(s);
    this.save('naspos.menu', [...s]);
  }

  toggleRail(): void {
    if (window.innerWidth <= 820) { this.mobileOpen.set(!this.mobileOpen()); return; }
    clearTimeout(this.peekTimer);
    this.peek.set(false);
    this.rail.set(!this.rail());
    this.save('naspos.rail', this.rail());
  }

  async openPalette(): Promise<void> {
    this.pq = '';
    this.pActive.set(0);
    this.palette.set(true);
    setTimeout(() => this.pbox?.nativeElement.focus());
    if (!this.reports().length) {
      try { this.reports.set(await this.api.get<ReportDefinition[]>('/reports')); } catch { /* reports optional */ }
    }
  }

  paletteKey(e: KeyboardEvent): void {
    const n = this.pResults().length;
    if (e.key === 'ArrowDown') { e.preventDefault(); this.pActive.set(Math.min(this.pActive() + 1, n - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); this.pActive.set(Math.max(this.pActive() - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const it = this.pResults()[this.pActive()]; if (it) this.go(it); }
    else if (e.key === 'Escape') { this.palette.set(false); }
    this.pTick.update(t => t + 1);
  }

  go(it: PaletteItem): void {
    this.palette.set(false);
    this.router.navigateByUrl(it.link);
  }

  @HostListener('window:keydown', ['$event'])
  keys(e: KeyboardEvent): void {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); this.openPalette(); }
    else if (e.key === 'F9' && this.auth.can('POS', 'Add')) { e.preventDefault(); this.router.navigate(['/pos']); }
    else if (e.key === 'Escape' && this.userMenu()) this.userMenu.set(false);
  }

  async switchCompany(id: number): Promise<void> {
    try {
      await this.ui.run(() => this.auth.switchTo(id));
      await this.live.stop();
      this.live.start();
      this.router.navigate(['/dashboard']);
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async switchYear(id: number): Promise<void> {
    try {
      await this.ui.run(() => this.auth.switchTo(this.auth.session()!.company.id, id));
      this.ui.ok('Financial year changed.');
      this.router.navigateByUrl('/dashboard');
    } catch (e) { this.ui.error(errorMessage(e)); }
  }

  async setCounter(): Promise<void> {
    this.userMenu.set(false);
    const v = await this.ui.prompt('Counter name', 'Name of this billing counter / computer', { value: this.auth.counter });
    if (v !== null) this.auth.counter = v.trim().substring(0, 40);
  }

  async toggleTheme(): Promise<void> {
    try { await this.theme.set(this.theme.isDark() ? 'light' : 'dark'); }
    catch (e) { this.ui.error(errorMessage(e)); }
  }

  async changePassword(): Promise<void> {
    this.userMenu.set(false);
    const current = await this.ui.prompt('Change password', 'Current password', { type: 'password' });
    if (!current) return;
    const next = await this.ui.prompt('Change password', 'New password (8+ chars, letters and digits)', { type: 'password' });
    if (!next) return;
    try {
      await this.api.post('/auth/change-password', { currentPassword: current, newPassword: next });
      this.ui.ok('Password changed. Other sessions were signed out.');
    } catch (e) { this.ui.error(errorMessage(e)); }
  }
}
