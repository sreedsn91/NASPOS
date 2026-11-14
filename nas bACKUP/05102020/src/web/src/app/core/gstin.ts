import { Pipe, PipeTransform } from '@angular/core';

const CHARS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ';
const STATES = new Set(['01', '02', '03', '04', '05', '06', '07', '08', '09', '10', '11', '12', '13', '14', '15', '16', '17', '18', '19', '20',
  '21', '22', '23', '24', '26', '27', '29', '30', '31', '32', '33', '34', '35', '36', '37', '38', '97', '99']);

export function normalizeGstin(v: string | null | undefined): string {
  return (v ?? '').replace(/[\s-]/g, '').toUpperCase();
}

export function gstinCheckChar(first14: string): string {
  let sum = 0;
  for (let i = 0; i < 14; i++) {
    const p = CHARS.indexOf(first14[i]) * (i % 2 === 0 ? 1 : 2);
    sum += Math.floor(p / 36) + (p % 36);
  }
  return CHARS[(36 - (sum % 36)) % 36];
}

/** Same rules and wording as the server (NasPos.Domain.Validation.Gstin.Error). Null when valid or empty. */
export function gstinError(v: string | null | undefined): string | null {
  const g = normalizeGstin(v);
  if (!g) return null;
  if (g.length !== 15) return `GSTIN must be 15 characters; you entered ${g.length}.`;
  if (![...g].every(c => CHARS.includes(c))) return 'GSTIN may contain only letters and digits.';
  if (!/^[0-9]{2}[0-9A-Z]{13}$/.test(g)) return 'GSTIN must start with a 2-digit state code.';
  if (!STATES.has(g.substring(0, 2))) return `'${g.substring(0, 2)}' is not a valid GST state code (first two digits).`;
  if (g[13] === 'Z' && !/^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/.test(g))
    return 'Characters 3–12 must be the PAN (5 letters, 4 digits, 1 letter) and character 13 the entity number (1–9 or A–Z).';
  if (g[14] !== gstinCheckChar(g.substring(0, 14)))
    return "The GSTIN's check character (last character) doesn't match — one of the characters is probably mistyped.";
  return null;
}

/** Error when the GSTIN's state (first two digits) differs from the selected state. */
export function gstinStateError(v: string | null | undefined, stateCode: string | null | undefined): string | null {
  const g = normalizeGstin(v);
  if (g.length < 2 || !stateCode || gstinError(g)) return null;
  return g.substring(0, 2) !== stateCode ? `This GSTIN belongs to state ${g.substring(0, 2)}, but state ${stateCode} is selected.` : null;
}

@Pipe({ name: 'gstinError', standalone: true })
export class GstinErrorPipe implements PipeTransform {
  transform(v: string | null | undefined, stateCode?: string | null): string | null {
    return gstinError(v) ?? (stateCode !== undefined ? gstinStateError(v, stateCode) : null);
  }
}
