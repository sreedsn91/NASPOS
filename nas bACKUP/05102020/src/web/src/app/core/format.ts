import { Pipe, PipeTransform } from '@angular/core';

const moneyFmt = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const qtyFmt = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });

export function money(v: unknown): string {
  const n = typeof v === 'number' ? v : Number(v);
  return v === null || v === undefined || v === '' || Number.isNaN(n) ? '' : moneyFmt.format(n);
}
export function qty(v: unknown): string {
  const n = typeof v === 'number' ? v : Number(v);
  return v === null || v === undefined || v === '' || Number.isNaN(n) ? '' : qtyFmt.format(n);
}
/** yyyy-mm-dd or ISO → dd-mm-yyyy */
export function dmy(v: unknown): string {
  if (!v) return '';
  const s = String(v);
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(s);
  if (!m) return s;
  if (s.length > 10 && s.includes('T')) {
    const d = new Date(s);
    return `${String(d.getDate()).padStart(2, '0')}-${String(d.getMonth() + 1).padStart(2, '0')}-${d.getFullYear()}`;
  }
  return `${m[3]}-${m[2]}-${m[1]}`;
}
export function dmyhm(v: unknown): string {
  if (!v) return '';
  const d = new Date(String(v));
  return `${dmy(v)} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
export function today(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
export function monthStart(): string { return today().substring(0, 8) + '01'; }
/** Local datetime-local value (yyyy-MM-ddTHH:mm) for an ISO string. */
export function toLocalInput(iso: string | null | undefined): string {
  const d = iso ? new Date(iso) : new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}
/** datetime-local value → ISO with the browser's offset. */
export function fromLocalInput(v: string): string {
  const d = new Date(v);
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  const p = (n: number) => String(Math.floor(Math.abs(n))).padStart(2, '0');
  return `${v.length === 16 ? v + ':00' : v}${sign}${p(off / 60)}:${p(off % 60)}`;
}

@Pipe({ name: 'money', standalone: true })
export class MoneyPipe implements PipeTransform { transform(v: unknown): string { return money(v); } }

@Pipe({ name: 'qty', standalone: true })
export class QtyPipe implements PipeTransform { transform(v: unknown): string { return qty(v); } }

@Pipe({ name: 'dmy', standalone: true })
export class DmyPipe implements PipeTransform { transform(v: unknown): string { return dmy(v); } }

@Pipe({ name: 'dmyhm', standalone: true })
export class DmyhmPipe implements PipeTransform { transform(v: unknown): string { return dmyhm(v); } }
