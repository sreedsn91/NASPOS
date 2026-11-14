import { HttpClient, HttpErrorResponse, HttpEvent, HttpHandlerFn, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, catchError, firstValueFrom, from, shareReplay, switchMap, throwError } from 'rxjs';
import { Right, RightName, Session } from './models';
import { Theme } from './theme';
import { Ui } from './ui';

interface TokenResponse { accessToken?: string; expiresAt?: string; companies: { id: number; name: string; roleName: string }[]; requiresCompany: boolean; }

@Injectable({ providedIn: 'root' })
export class Auth {
  private http = inject(HttpClient);
  private router = inject(Router);
  private theme = inject(Theme);
  /** Access token lives in memory only; the refresh token is an HttpOnly cookie. */
  readonly token = signal<string | null>(null);
  readonly session = signal<Session | null>(null);
  readonly isAdmin = computed(() => !!this.session()?.user.isAdmin);
  private refreshing$: Observable<boolean> | null = null;

  get counter(): string {
    try { return localStorage.getItem('kshethra.counter') || ''; } catch { return ''; }
  }
  set counter(v: string) {
    try { localStorage.setItem('kshethra.counter', v); } catch { /* storage unavailable */ }
  }

  can(screen: string, right: RightName = 'Read'): boolean {
    const s = this.session();
    if (!s) return false;
    if (s.user.isAdmin) return true;
    return ((s.rights[screen] ?? 0) & Right[right]) === Right[right];
  }

  flag(key: string): boolean { return (this.session()?.settings[key] ?? '').toLowerCase() === 'true'; }
  setting(key: string): string { return this.session()?.settings[key] ?? ''; }

  async init(): Promise<void> {
    try {
      if (await firstValueFrom(this.refresh())) await this.loadSession();
    } catch { /* not signed in */ }
  }

  async login(userName: string, password: string, companyId?: number): Promise<TokenResponse> {
    const r = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/login', { userName, password, companyId }));
    if (r.accessToken) { this.token.set(r.accessToken); await this.loadSession(); }
    return r;
  }

  async setup(body: unknown): Promise<void> {
    const r = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/setup', body));
    this.token.set(r.accessToken!);
    await this.loadSession();
  }

  async switchTo(companyId: number, financialYearId?: number): Promise<void> {
    const r = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/switch', { companyId, financialYearId }));
    this.token.set(r.accessToken!);
    await this.loadSession();
  }

  async loadSession(): Promise<void> {
    const s = await firstValueFrom(this.http.get<Session>('/api/auth/me'));
    this.theme.apply(s.user.theme); // each user's own theme, whichever computer they sign in on
    this.session.set(s);
  }

  refresh(): Observable<boolean> {
    if (!this.refreshing$) {
      const fy = this.session()?.financialYear?.id;
      this.refreshing$ = this.http.post<TokenResponse>('/api/auth/refresh', {}, { params: fy ? { fy } : {} }).pipe(
        switchMap(r => { this.token.set(r.accessToken ?? null); this.refreshing$ = null; return from([!!r.accessToken]); }),
        catchError(e => { this.refreshing$ = null; this.token.set(null); return throwError(() => e); }),
        shareReplay(1),
      );
    }
    return this.refreshing$;
  }

  async logout(): Promise<void> {
    try { await firstValueFrom(this.http.post('/api/auth/logout', {})); } catch { /* ignore */ }
    this.token.set(null);
    this.session.set(null);
    await this.router.navigate(['/login']);
  }

  async stepUp(ui: Ui): Promise<string | null> {
    const pwd = await ui.prompt('Confirm it is you', 'Password', { type: 'password', message: 'This action needs your password again.', okText: 'Confirm' });
    if (!pwd) return null;
    const r = await firstValueFrom(this.http.post<{ token: string }>('/api/auth/step-up', { password: pwd }));
    return r.token;
  }
}

/**
 * Adds the bearer token and counter name; transparently refreshes an expired token, asks for the password on
 * step-up-required responses, and asks the user to confirm business warnings (credit limit / negative stock).
 */
export const authInterceptor: HttpInterceptorFn = (req: HttpRequest<unknown>, next: HttpHandlerFn): Observable<HttpEvent<unknown>> => {
  const auth = inject(Auth);
  const ui = inject(Ui);
  const router = inject(Router);
  const decorate = (r: HttpRequest<unknown>) => {
    const headers: Record<string, string> = {};
    const t = auth.token();
    if (t && r.url.startsWith('/api')) headers['Authorization'] = `Bearer ${t}`;
    if (auth.counter) headers['X-Counter'] = auth.counter;
    return r.clone({ setHeaders: headers });
  };

  return next(decorate(req)).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || !req.url.startsWith('/api')) return throwError(() => err);
      const code = err.error && typeof err.error === 'object' ? err.error.code : undefined;

      if (err.status === 401 && !req.url.startsWith('/api/auth/')) {
        return auth.refresh().pipe(
          switchMap(() => next(decorate(req))),
          catchError(e => { auth.token.set(null); auth.session.set(null); router.navigate(['/login']); return throwError(() => e); }),
        );
      }
      if (err.status === 403 && code === 'step_up_required') {
        return from(auth.stepUp(ui)).pipe(switchMap(token => token
          ? next(decorate(req.clone({ setHeaders: { 'X-Step-Up-Token': token } })))
          : throwError(() => err)));
      }
      if (err.status === 409 && code === 'confirm_required' && req.body && typeof req.body === 'object') {
        const warnings: string[] = err.error.warnings ?? [];
        return from(ui.confirm('Please confirm', 'Save anyway?', { items: warnings, okText: 'Save anyway' })).pipe(switchMap(ok => ok
          ? next(decorate(req.clone({ body: { ...(req.body as object), acknowledgeWarnings: true } })))
          : throwError(() => err)));
      }
      return throwError(() => err);
    }),
  );
};

export const authGuard: CanActivateFn = () => {
  const auth = inject(Auth);
  return auth.session() ? true : inject(Router).createUrlTree(['/login']);
};

export function screenGuard(screen: string, right: RightName = 'Read'): CanActivateFn {
  return () => {
    const auth = inject(Auth);
    if (auth.can(screen, right)) return true;
    inject(Ui).error(`You do not have access to ${screen}.`);
    return inject(Router).createUrlTree(['/']);
  };
}
