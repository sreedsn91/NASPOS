import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ThemeMode = 'light' | 'dark' | 'system' | 'ocean' | 'sunset';
const KEY = 'kshethra.theme';
const MODES: ThemeMode[] = ['light', 'dark', 'system', 'ocean', 'sunset'];

/**
 * UI theme. The user's choice is stored on their account (so it follows them to every counter) and cached in
 * localStorage only so the login screen opens in the last-used theme without a flash.
 */
@Injectable({ providedIn: 'root' })
export class Theme {
  private http = inject(HttpClient);
  private media = matchMedia('(prefers-color-scheme: dark)');
  private systemDark = signal(this.media.matches);
  readonly mode = signal<ThemeMode>(Theme.cached());
  /** What is actually on screen (system resolved). */
  readonly isDark = computed(() => this.mode() === 'dark' || (this.mode() === 'system' && this.systemDark()));

  constructor() {
    this.media.addEventListener('change', e => this.systemDark.set(e.matches));
    this.apply(this.mode());
  }

  private static cached(): ThemeMode {
    try { const v = localStorage.getItem(KEY) as ThemeMode; return MODES.includes(v) ? v : 'light'; } catch { return 'light'; }
  }

  /** Apply without saving to the server (used when a session loads). */
  apply(mode: string | null | undefined): void {
    const m: ThemeMode = MODES.includes(mode as ThemeMode) ? mode as ThemeMode : 'light';
    this.mode.set(m);
    // 'system' removes the attribute so the prefers-color-scheme rule in styles.scss decides.
    if (m === 'system') document.documentElement.removeAttribute('data-theme');
    else document.documentElement.setAttribute('data-theme', m);
    try { localStorage.setItem(KEY, m); } catch { /* storage unavailable */ }
  }

  /** User changed the theme: apply now and remember it on their account. */
  async set(mode: ThemeMode): Promise<void> {
    const previous = this.mode();
    this.apply(mode);
    try { await firstValueFrom(this.http.put('/api/auth/me/theme', { theme: mode })); }
    catch (e) { this.apply(previous); throw e; }
  }
}
