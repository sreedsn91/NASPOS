import { LANDED_COST, ProductLookup, RoundOffMode, TAX_FREE, TaxType, TradeDocument, TradeLine, VoucherType } from './models';

/** Round half away from zero (matches the server's MidpointRounding.AwayFromZero). */
export function round(v: number, dp = 2): number {
  const f = Math.pow(10, dp);
  const x = Math.abs(v) * f;
  const r = Math.round(x + 1e-9 * Math.max(1, x));
  return (Math.sign(v) * r) / f || 0;
}
export const r2 = (v: number) => round(v, 2);
export const r4 = (v: number) => round(v, 4);

export function roundOff(amount: number, mode: RoundOffMode): number {
  if (amount < 0) return -roundOff(-amount, mode);
  const whole = Math.trunc(amount);
  const frac = r2(amount - whole);
  switch (mode) {
    case 'Auto': return frac >= 0.5 ? whole + 1 : whole;
    case 'Up': return frac === 0 ? whole : frac <= 0.5 ? whole + 0.5 : whole + 1;
    case 'Down': return frac >= 0.5 ? whole + 0.5 : whole;
    default: return r2(amount);
  }
}

/**
 * Mirror of the server's FitInclusiveTax: a tax-inclusive line totals exactly the price the customer was quoted.
 * The total tax is the inclusive amount less the taxable value, split across the taxes by rate; the last one takes
 * the rounding paisa (₹60 at 18% → CGST 4.58 + SGST 4.57, not 4.58 + 4.58 = ₹60.01).
 */
function fitInclusiveTax(l: TradeLine, cg: number, sg: number, ig: number): void {
  const back = cg + sg + ig + l.cessRate + l.kfCessRate;
  if (back <= 0) return;
  const target = !l.discountAmount && !l.billDiscountShare ? r2((l.qty || 0) * (l.rate || 0)) : r2((l.taxable * (100 + back)) / 100);
  const totalTax = r2(target - l.taxable);
  const parts: [number, (v: number) => void][] = [
    [cg, v => (l.cgstAmount = v)], [sg, v => (l.sgstAmount = v)], [ig, v => (l.igstAmount = v)],
    [l.cessRate, v => (l.cessAmount = v)], [l.kfCessRate, v => (l.kfCessAmount = v)],
  ];
  let last = -1;
  parts.forEach(([rate], i) => { if (rate > 0) last = i; });
  let given = 0;
  parts.forEach(([rate, set], i) => {
    if (rate <= 0) return;
    const v = i === last ? r2(totalTax - given) : r2((totalTax * rate) / back);
    set(v);
    given = r2(given + v);
  });
}

export interface CalcOptions { serviceTaxRate: number; serviceCessRate: number; taxClaimable: boolean; }

/**
 * Client mirror of the server's DocumentCalculator, for instant totals while typing. The server recalculates
 * authoritatively on save; the saved document shows the server's figures.
 */
export function calculate(doc: TradeDocument, opt: CalcOptions): void {
  const intra = doc.taxType === 'IntraState';
  const inter = doc.taxType === 'InterState';
  const lines = doc.lines.filter(l => l.productId > 0);
  for (const l of lines) {
    const cg = intra ? l.cgstRate : 0, sg = intra ? l.sgstRate : 0, ig = inter ? l.igstRate : 0;
    const back = cg + sg + ig + l.cessRate + l.kfCessRate;
    const value = (l.qty || 0) * (l.rate || 0);
    l.gross = l.taxInclusive && back > 0 ? r2((value * 100) / (100 + back)) : r2(value);
    if (l.discountPercent > 0) l.discountAmount = r2((l.gross * l.discountPercent) / 100);
    (l as any)._cg = cg; (l as any)._sg = sg; (l as any)._ig = ig;
  }
  const sumAmount = lines.reduce((s, l) => s + l.gross - l.discountAmount, 0);
  const billDisc = doc.billDiscountPercent > 0 ? r2((sumAmount * doc.billDiscountPercent) / 100) : doc.billDiscount || 0;
  let remaining = billDisc;
  lines.forEach((l, i) => {
    const amount = l.gross - l.discountAmount;
    l.billDiscountShare = i === lines.length - 1 ? r2(remaining) : sumAmount === 0 ? 0 : r2((billDisc * amount) / sumAmount);
    remaining -= l.billDiscountShare;
  });
  for (const l of lines) {
    const x = l as any;
    l.taxable = r2(l.gross - l.discountAmount - l.billDiscountShare);
    l.cgstAmount = r2((l.taxable * x._cg) / 100);
    l.sgstAmount = r2((l.taxable * x._sg) / 100);
    l.igstAmount = r2((l.taxable * x._ig) / 100);
    l.cessAmount = r2((l.taxable * l.cessRate) / 100);
    l.addCessAmount = r2((l.qty || 0) * l.addCessRate);
    l.kfCessAmount = r2((l.taxable * l.kfCessRate) / 100);
    if (l.taxInclusive) fitInclusiveTax(l, x._cg, x._sg, x._ig);
    l.lineTotal = r2(l.taxable + l.cgstAmount + l.sgstAmount + l.igstAmount + l.cessAmount + l.addCessAmount + l.kfCessAmount);
  }
  const sum = (f: (l: TradeLine) => number) => r2(lines.reduce((s, l) => s + f(l), 0));
  doc.grossAmount = sum(l => l.gross);
  doc.itemDiscount = sum(l => l.discountAmount);
  doc.taxableAmount = sum(l => ((l as any)._cg + (l as any)._sg + (l as any)._ig > 0 ? l.taxable : 0));
  doc.exemptAmount = sum(l => ((l as any)._cg + (l as any)._sg + (l as any)._ig > 0 ? 0 : l.taxable));
  doc.cgstAmount = sum(l => l.cgstAmount);
  doc.sgstAmount = sum(l => l.sgstAmount);
  doc.igstAmount = sum(l => l.igstAmount);
  doc.cessAmount = sum(l => l.cessAmount);
  doc.addCessAmount = sum(l => l.addCessAmount);
  doc.kfCessAmount = sum(l => l.kfCessAmount);
  doc.totalMrp = sum(l => (l.qty || 0) * (l.mrp || 0));
  const serviceTaxable = sum(l => (l.itemClass === 'Service' ? l.taxable : 0));
  doc.serviceTaxAmount = r2((serviceTaxable * opt.serviceTaxRate) / 100);
  doc.serviceCessAmount = r2((doc.serviceTaxAmount * opt.serviceCessRate) / 100);
  doc.otherExpenseTax = doc.taxType === 'NoTax' ? 0 : r2(((doc.otherExpense || 0) * (doc.otherExpenseTaxRate || 0)) / 100);
  doc.freightTax = doc.taxType === 'NoTax' ? 0 : r2(((doc.freight || 0) * (doc.freightTaxRate || 0)) / 100);
  const pre = r2(sum(l => l.lineTotal) + (doc.otherExpense || 0) + doc.otherExpenseTax + (doc.freight || 0) + doc.freightTax + doc.serviceTaxAmount + doc.serviceCessAmount);
  const rounded = roundOff(pre, doc.roundOffMode ?? 'Auto');
  doc.roundOff = r2(rounded - pre);
  const cashDisc = doc.cashDiscountPercent > 0 ? r2((rounded * doc.cashDiscountPercent) / 100) : doc.cashDiscount || 0;
  doc.billAmount = r2(rounded - cashDisc - (doc.redeemAmount || 0));

  if (LANDED_COST.includes(doc.voucherType)) {
    const sumTaxable = sum(l => l.taxable);
    const charges = (doc.otherExpense || 0) + (doc.freight || 0);
    const factor = doc.costFactor > 0 ? doc.costFactor : 1;
    for (const l of lines) {
      const baseQty = ((l.qty || 0) + (l.freeQty || 0)) * (l.unitFactor || 1);
      if (!baseQty) { l.cost = 0; continue; }
      let value = l.taxable + (sumTaxable ? (charges * l.taxable) / sumTaxable : 0);
      if (!opt.taxClaimable) value += l.cgstAmount + l.sgstAmount + l.igstAmount + l.cessAmount + l.addCessAmount + l.kfCessAmount;
      l.cost = r4((value * factor) / baseQty);
      const mrpBase = (l.mrp || 0) / (l.unitFactor || 1);
      l.profit = r2((mrpBase - l.cost) * baseQty);
      l.profitPercent = l.cost ? r2(((mrpBase - l.cost) * 100) / l.cost) : 0;
    }
  } else {
    for (const l of lines) {
      const baseQty = ((l.qty || 0) + (l.freeQty || 0)) * (l.unitFactor || 1);
      l.profit = r2(l.taxable - (l.cost || 0) * baseQty);
      const c = (l.cost || 0) * baseQty;
      l.profitPercent = c ? r2((l.profit * 100) / c) : 0;
    }
  }
  doc.cost = sum(l => (l.cost || 0) * ((l.qty || 0) + (l.freeQty || 0)) * (l.unitFactor || 1));
  doc.profit = r2(sum(l => l.taxable) - doc.cost - cashDisc);
}

export function emptyDocument(type: VoucherType, roundOffMode: RoundOffMode = 'Auto'): TradeDocument {
  return {
    id: 0, voucherType: type, date: new Date().toISOString(), paymentMode: 'Cash', taxType: null, billType: null,
    billDiscountPercent: 0, billDiscount: 0, otherExpense: 0, otherExpenseTaxRate: 0, freight: 0, freightTaxRate: 0, roundOffMode,
    cashDiscountPercent: 0, cashDiscount: 0, costFactor: 1, cashAmount: 0, cardAmount: 0, creditAmount: 0, tenderedAmount: 0,
    pointsRedeemed: 0, labourCost: 0, otherCost: 0, grossAmount: 0, itemDiscount: 0, taxableAmount: 0, exemptAmount: 0, cgstAmount: 0,
    sgstAmount: 0, igstAmount: 0, cessAmount: 0, addCessAmount: 0, kfCessAmount: 0, serviceTaxAmount: 0, serviceCessAmount: 0,
    otherExpenseTax: 0, freightTax: 0, roundOff: 0, billAmount: 0, totalMrp: 0, cost: 0, profit: 0, pointsEarned: 0, redeemAmount: 0,
    lines: [],
  };
}

export function emptyLine(): TradeLine {
  return {
    productId: 0, unitFactor: 1, isAltUnit: false, qty: 1, freeQty: 0, mrp: 0, rate: 0, taxInclusive: false, gross: 0, discountPercent: 0,
    discountAmount: 0, billDiscountShare: 0, taxable: 0, cgstRate: 0, cgstAmount: 0, sgstRate: 0, sgstAmount: 0, igstRate: 0, igstAmount: 0,
    cessRate: 0, cessAmount: 0, addCessRate: 0, addCessAmount: 0, kfCessRate: 0, kfCessAmount: 0, lineTotal: 0, saleRate: 0, cost: 0,
    profit: 0, profitPercent: 0,
  };
}

/** Builds a line from a product lookup row using the rules the server applies (tax from master, secondary slab). */
export function lineFromProduct(p: ProductLookup, type: VoucherType, opts: { inclusive?: boolean; kfCess: boolean; purchase: boolean }): TradeLine {
  const l = emptyLine();
  l.productId = p.productId;
  l.productCode = p.code;
  l.productName = p.name;
  l.regionalName = p.regionalName;
  l.hsnCode = p.hsnCode;
  l.itemClass = p.itemClass;
  l.batchId = p.batchId ?? null;
  l.batchCode = p.batchCode ?? null;
  l.unit = p.mainUnit;
  l.mainUnit = p.mainUnit;
  l.altUnit = p.altUnit;
  l.conversionFactor = p.conversionFactor;
  l.mrp = p.mrp;
  l.rate = opts.purchase ? p.purchaseRate : p.price;
  l.saleRate = p.saleRate;
  l.cost = p.cost;
  l.stock = p.stock;
  l.trackExpiry = p.trackExpiry;
  l.expiryDate = p.expiryDate ?? null;
  l.discountPercent = opts.purchase ? 0 : p.discountPercent;
  l.taxInclusive = opts.inclusive ?? (opts.purchase ? p.purchaseTaxInclusive : p.saleTaxInclusive);
  l.pCgst = p.cgstRate; l.pSgst = p.sgstRate; l.pIgst = p.igstRate; l.pCess = p.cessRate; l.pAddCess = p.addCessPerUnit;
  l.pKf = opts.kfCess && !opts.purchase ? p.kfCessRate : 0;
  l.hasSecondaryTax = p.hasSecondaryTax; l.secondaryTaxThreshold = p.secondaryTaxThreshold;
  l.p2Cgst = p.cgst2Rate; l.p2Sgst = p.sgst2Rate; l.p2Igst = p.igst2Rate;
  if (p.scaleQty) l.qty = p.scaleQty;
  applyTax(l, type);
  return l;
}

export function applyTax(l: TradeLine, type: VoucherType): void {
  const secondary = !!l.hasSecondaryTax && (l.secondaryTaxThreshold ?? 0) > 0 && l.rate / (l.unitFactor || 1) > (l.secondaryTaxThreshold ?? 0);
  l.cgstRate = (secondary ? l.p2Cgst : l.pCgst) ?? l.cgstRate;
  l.sgstRate = (secondary ? l.p2Sgst : l.pSgst) ?? l.sgstRate;
  l.igstRate = (secondary ? l.p2Igst : l.pIgst) ?? l.igstRate;
  l.cessRate = l.pCess ?? l.cessRate;
  l.addCessRate = (l.pAddCess ?? 0) * (l.unitFactor || 1);
  l.kfCessRate = l.pKf ?? 0;
  if (TAX_FREE.includes(type)) { l.cgstRate = l.sgstRate = l.igstRate = l.cessRate = l.addCessRate = l.kfCessRate = 0; l.taxInclusive = false; }
}

export function defaultTaxType(companyState: string | undefined, partyState: string | undefined | null, partyGstin?: string | null): TaxType {
  const ps = partyState || (partyGstin ? partyGstin.substring(0, 2) : '');
  return ps && companyState && ps !== companyState ? 'InterState' : 'IntraState';
}
