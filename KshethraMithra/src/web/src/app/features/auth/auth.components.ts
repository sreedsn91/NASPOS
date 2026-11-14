import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { GstinErrorPipe, gstinError, gstinStateError, normalizeGstin } from '../../core/gstin';
import { IconComponent } from '../../shared/icon';
import { LogoComponent } from '../../shared/logo';

/** Brand panel shown beside the login and setup forms. */
@Component({
  selector: 'app-auth-hero',
  standalone: true,
  imports: [IconComponent, LogoComponent],
  host: { class: 'auth-hero' },
  template: `
    <div class="brand" style="display:flex;gap:10px;align-items:center">
      <app-logo class="brand-logo" [size]="40" /><b style="font-size:17px">Kshethra Mithra</b>
    </div>
    <div>
      <h2>Vazhipadu, poojas and accounts for your temple — in one place.</h2>
      <p>Offering bookings, the daily pooja list, Malayalam calendar, donations and double-entry books that always balance.</p>
      <ul>
        <li><app-icon name="star" /> Vazhipadu counter with devotee name and star, for one day or every Friday, Karthika or Ekadashi</li>
        <li><app-icon name="calendar" /> Malayalam calendar with the day's star and tithi, and the pooja list for priests</li>
        <li><app-icon name="cash" /> Donation receipts, pooja-item stock and GST-ready accounts</li>
        <li><app-icon name="shield" /> Role-based access, audit log and safe backups</li>
      </ul>
    </div>
    <small style="color:#94a3c8">Made for Kerala temples · Works on any browser</small>`,
})
export class AuthHeroComponent {}

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule, AuthHeroComponent, LogoComponent],
  template: `
    <div class="auth-page">
      <app-auth-hero />
      <div class="auth-side">
      <form class="auth-card stack" (ngSubmit)="submit()">
        <div>
          <app-logo class="card-logo" [size]="44" />
          <h1>{{ companies().length ? 'Choose a temple' : 'Welcome back' }}</h1>
          <div class="muted">{{ companies().length ? 'You have access to more than one temple.' : 'Sign in to continue to your temple.' }}</div>
        </div>
        @if (error()) { <div class="error-box" role="alert">{{ error() }}</div> }
        @if (!companies().length) {
          <label class="field"><span>User name</span><input name="u" [(ngModel)]="userName" autocomplete="username" required autofocus /></label>
          <label class="field"><span>Password</span><input name="p" type="password" [(ngModel)]="password" autocomplete="current-password" required /></label>
        } @else {
          <label class="field"><span>Temple</span>
            <select name="c" [(ngModel)]="companyId">
              @for (c of companies(); track c.id) { <option [ngValue]="c.id">{{ c.name }} — {{ c.roleName }}</option> }
            </select>
          </label>
        }
        <label class="field"><span>Counter / computer name (optional)</span><input name="counter" [(ngModel)]="counter" placeholder="e.g. COUNTER-1" /></label>
        <button class="btn primary lg" type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : (companies().length ? 'Open temple' : 'Sign in') }}</button>
      </form>
      </div>
    </div>`,
})
export class LoginComponent implements OnInit {
  private auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  userName = '';
  password = '';
  counter = this.auth.counter;
  companyId: number | null = null;
  companies = signal<{ id: number; name: string; roleName: string }[]>([]);
  error = signal('');
  busy = signal(false);

  async ngOnInit(): Promise<void> {
    if (this.auth.session()) { this.router.navigate(['/']); return; }
    try {
      const s = await this.api.get<{ setupRequired: boolean }>('/auth/setup-status');
      if (s.setupRequired) this.router.navigate(['/setup']);
    } catch (e) { this.error.set(errorMessage(e)); }
  }

  async submit(): Promise<void> {
    this.error.set('');
    this.busy.set(true);
    try {
      this.auth.counter = this.counter.trim();
      const r = await this.auth.login(this.userName, this.password, this.companyId ?? undefined);
      if (r.requiresCompany) {
        this.companies.set(r.companies);
        this.companyId = r.companies[0]?.id ?? null;
        return;
      }
      this.password = '';
      this.router.navigate(['/']);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-setup',
  standalone: true,
  imports: [FormsModule, GstinErrorPipe, AuthHeroComponent, LogoComponent],
  template: `
    <div class="auth-page">
      <app-auth-hero />
      <div class="auth-side">
      <form class="auth-card wide stack" (ngSubmit)="submit()">
        <div>
          <app-logo class="card-logo" [size]="44" />
          <h1>Welcome — first-time setup</h1>
          <div class="muted">Create your temple and the administrator account. No default password is shipped.</div>
        </div>
        @if (error()) { <div class="error-box" role="alert">{{ error() }}</div> }
        <div class="form-grid cols-2">
          <label class="field req span-2"><span>Temple name</span><input name="cn" [(ngModel)]="m.companyName" required /></label>
          <label class="field req"><span>State</span>
            <select name="st" [(ngModel)]="m.stateCode" required>
              @for (s of states(); track s[0]) { <option [value]="s[0]">{{ s[0] }} — {{ s[1] }}</option> }
            </select>
          </label>
          <label class="field"><span>GSTIN (optional)</span><input name="g" [(ngModel)]="m.gstin" (ngModelChange)="gstinChanged()" maxlength="20" style="text-transform:uppercase" />
            @if (m.gstin | gstinError: m.stateCode; as err) { <small class="danger-text">{{ err }}</small> }</label>
          <label class="field span-2"><span>Address</span><input name="a" [(ngModel)]="m.address" /></label>
          <label class="field"><span>City</span><input name="ci" [(ngModel)]="m.city" /></label>
          <label class="field"><span>Phone</span><input name="ph" [(ngModel)]="m.phone" /></label>
          <label class="field"><span>Financial year starts</span><input name="fy" type="date" [(ngModel)]="m.financialYearStart" /></label>
          <span></span>
          <label class="field req span-2"><span>License key</span>
            <textarea name="lk" rows="3" [(ngModel)]="m.licenseKey" required spellcheck="false" class="mono" placeholder="NAS1-XXXXX-XXXXX-…"></textarea>
            <small class="muted">The key you received from Kshethra Mithra support. It is issued for your temple name (and GSTIN, if given) — enter them exactly as given when you bought it.</small></label>
          <div class="span-2 muted small" style="border-top:1px solid var(--line);padding-top:10px;font-weight:600">Administrator account</div>
          <label class="field req"><span>Admin user name</span><input name="au" [(ngModel)]="m.adminUserName" autocomplete="username" required /></label>
          <label class="field"><span>Admin display name</span><input name="ad" [(ngModel)]="m.adminDisplayName" /></label>
          <label class="field req"><span>Admin password (8+ chars, letters and digits)</span><input name="ap" type="password" [(ngModel)]="m.adminPassword" autocomplete="new-password" required /></label>
          <label class="field req"><span>Confirm password</span><input name="ap2" type="password" [(ngModel)]="confirm" autocomplete="new-password" required /></label>
        </div>
        <button class="btn primary lg" type="submit" [disabled]="busy()">{{ busy() ? 'Creating…' : 'Create temple' }}</button>
      </form>
      </div>
    </div>`,
})
export class SetupComponent implements OnInit {
  private auth = inject(Auth);
  private api = inject(Api);
  private router = inject(Router);
  states = signal<[string, string][]>([]);
  error = signal('');
  busy = signal(false);
  confirm = '';
  m = { companyName: '', stateCode: '32', gstin: '', address: '', city: '', phone: '', adminUserName: 'admin', adminDisplayName: '', adminPassword: '', financialYearStart: null as string | null, licenseKey: '' };

  async ngOnInit(): Promise<void> {
    const s = await this.api.get<{ setupRequired: boolean; states: Record<string, string> }>('/auth/setup-status');
    if (!s.setupRequired) { this.router.navigate(['/login']); return; }
    this.states.set(Object.entries(s.states));
  }

  /** A complete, valid GSTIN fixes the state (its first two digits). */
  gstinChanged(): void {
    const g = normalizeGstin(this.m.gstin);
    if (g.length === 15 && !gstinError(g) && this.states().some(s => s[0] === g.substring(0, 2))) this.m.stateCode = g.substring(0, 2);
  }

  async submit(): Promise<void> {
    this.error.set('');
    const gErr = gstinError(this.m.gstin) ?? gstinStateError(this.m.gstin, this.m.stateCode);
    if (gErr) { this.error.set(gErr + ' Leave GSTIN empty if you are not registered yet — you can add it later in Temple Profile.'); return; }
    if (this.m.adminPassword !== this.confirm) { this.error.set('Passwords do not match.'); return; }
    if (!this.m.licenseKey.trim()) { this.error.set('Enter the license key. Contact Kshethra Mithra support to get one.'); return; }
    this.busy.set(true);
    try {
      await this.auth.setup({ ...this.m, gstin: normalizeGstin(this.m.gstin) || null, financialYearStart: this.m.financialYearStart || null });
      this.router.navigate(['/settings/features']);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
