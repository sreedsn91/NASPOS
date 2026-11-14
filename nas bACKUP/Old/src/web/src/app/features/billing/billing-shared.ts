import { Api } from '../../core/api';
import { Employee, IdName, PartyLookup, PartyType, ProductLookup, VoucherType } from '../../core/models';
import { money, qty } from '../../core/format';

export interface InvoiceOptions {
  printOnSave: boolean; confirmPrint: boolean; showPreview: boolean; inclusiveUnitPrice: boolean; freeQty: boolean; extraColumn: boolean;
  extraColumnName: string; productRemarks: boolean; disableRateEdit: boolean; scannerMode: boolean; cashDeskOnSave: boolean;
  printSchemeId?: number | null; focusNextRow: boolean; customerAutoSave: boolean; deliveryDate: boolean; amountDecimals: number;
  qtyDecimals: number; sendSms: boolean; smsTemplate?: string | null; hiddenColumns: string[];
}

export function partyTypeFor(t: VoucherType): PartyType | null {
  if (['Purchase', 'LocalPurchase', 'PurchaseReturn', 'PurchaseOrder', 'ReceiptNote'].includes(t)) return 'Supplier';
  if (['OpeningStock', 'StockAdjustment', 'Production'].includes(t)) return null;
  return 'Customer';
}

export function productLabel(p: ProductLookup): string { return `${p.name}${p.batchCode && p.batchCode !== p.code ? ' · ' + p.batchCode : ''}`; }
export function productSub(p: ProductLookup): string {
  return `${p.code} · stock ${qty(p.stock)} · ₹${money(p.price)}${p.priceSource !== 'Rate' ? ' (' + p.priceSource + ')' : ''}${p.mrp ? ' · MRP ' + money(p.mrp) : ''}`;
}
export function partyLabel(p: PartyLookup): string { return p.name; }
export function partySub(p: PartyLookup): string { return `${p.code}${p.mobile ? ' · ' + p.mobile : ''} · bal ${money(p.balance)}`; }

/** Cached reference data for billing screens. */
export class BillingRefs {
  employees: Employee[] = [];
  priceLists: IdName[] = [];
  bankLedgers: IdName[] = [];
  constructor(private api: Api) {}
  async load(): Promise<void> {
    const [emps, lists, groups] = await Promise.all([
      this.api.get<Employee[]>('/employees', { activeOnly: true }),
      this.api.get<{ id: number; name: string; isActive: boolean }[]>('/lookups/pricelist', { activeOnly: true }),
      this.api.get<{ id: number; systemCode?: string }[]>('/account-groups'),
    ]);
    this.employees = emps.filter(e => e.isSalesperson);
    this.priceLists = lists.map(l => ({ id: l.id, name: l.name }));
    const bank = groups.find(g => g.systemCode === 'BankAccounts');
    if (bank) {
      const page = await this.api.get<{ items: { id: number; name: string }[] }>('/ledgers', { groupId: bank.id, active: true, pageSize: 200 });
      this.bankLedgers = page.items.map(l => ({ id: l.id, name: l.name }));
    }
  }
}

export function lastSalesperson(): number | null {
  try { const v = localStorage.getItem('naspos.salesperson'); return v ? Number(v) : null; } catch { return null; }
}
export function rememberSalesperson(id: number | null | undefined): void {
  try { if (id) localStorage.setItem('naspos.salesperson', String(id)); } catch { /* ignore */ }
}
