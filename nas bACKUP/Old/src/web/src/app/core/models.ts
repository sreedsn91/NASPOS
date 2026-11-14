export type VoucherType =
  | 'Sales' | 'LocalSales' | 'Estimate' | 'SalesOrder' | 'SalesReturn' | 'DeliveryNote' | 'Purchase' | 'LocalPurchase'
  | 'ReceiptNote' | 'PurchaseReturn' | 'Receipt' | 'Payment' | 'Journal' | 'Contra' | 'DebitNote' | 'CreditNote'
  | 'OpeningStock' | 'StockAdjustment' | 'Production' | 'PurchaseOrder' | 'POSSales' | 'Opening';

export type PaymentMode = 'Cash' | 'Credit' | 'Mixed';
export type TaxType = 'IntraState' | 'InterState' | 'NoTax';
export type BillType = 'B2C' | 'B2B';
export type ItemClass = 'Goods' | 'Service';
export type RoundOffMode = 'Auto' | 'Up' | 'Down' | 'None';
export type EntrySide = 'Dr' | 'Cr';
export type StockDirection = 'Out' | 'None' | 'In';
export type PartyType = 'None' | 'Customer' | 'Supplier' | 'Employee';

export const Right = { Read: 1, Add: 2, Edit: 4, Cancel: 8, Delete: 16, Print: 32, Export: 64, Email: 128, Sms: 256, All: 511 } as const;
export type RightName = keyof typeof Right;

export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; }
export interface IdName { id: number; name: string; }

export interface CompanyDto {
  id: number; name: string; legalName?: string; address1?: string; address2?: string; city?: string; state?: string; stateCode: string;
  country?: string; postalCode?: string; phone?: string; email?: string; gstin?: string; pan?: string; website?: string;
  invoiceFooter?: string; hasLogo?: boolean; isActive?: boolean; isHidden?: boolean; financialYearStart?: string | null;
}

export interface FinancialYear { id: number; name: string; startDate: string; endDate: string; isClosed: boolean; closedAt?: string; }

export interface Session {
  user: { id: number; userName: string; displayName: string; isAdmin: boolean; theme?: string };
  company: CompanyDto;
  financialYear: FinancialYear;
  years: FinancialYear[];
  rights: Record<string, number>;
  settings: Record<string, string>;
  counter?: string;
}

export interface TradeLine {
  id?: number; lineNo?: number; productId: number; productCode?: string; productName?: string; regionalName?: string;
  batchId?: number | null; batchCode?: string | null; direction?: StockDirection; itemClass?: ItemClass; hsnCode?: string;
  unit?: string; unitFactor: number; isAltUnit: boolean; altUnit?: string | null; mainUnit?: string; conversionFactor?: number;
  qty: number; freeQty: number; mrp: number; rate: number; taxInclusive: boolean;
  gross: number; discountPercent: number; discountAmount: number; billDiscountShare: number; taxable: number;
  cgstRate: number; cgstAmount: number; sgstRate: number; sgstAmount: number; igstRate: number; igstAmount: number;
  cessRate: number; cessAmount: number; addCessRate: number; addCessAmount: number; kfCessRate: number; kfCessAmount: number;
  lineTotal: number; saleRate: number; expiryDate?: string | null; cost: number; profit: number; profitPercent: number;
  extraColumn?: string; patternNo?: string; remarks?: string; stock?: number; trackExpiry?: boolean;
  // product master rates for re-applying tax when tax type changes
  pCgst?: number; pSgst?: number; pIgst?: number; pCess?: number; pAddCess?: number; pKf?: number;
  hasSecondaryTax?: boolean; secondaryTaxThreshold?: number; p2Cgst?: number; p2Sgst?: number; p2Igst?: number;
}

export interface TradeDocument {
  id: number; voucherType: VoucherType; documentNo?: string; date: string; version?: number; rowVersion?: string | null;
  acknowledgeWarnings?: boolean;
  partyLedgerId?: number | null; partyName?: string; partyAddress?: string; partyPhone?: string; partyGstin?: string; partyStateCode?: string;
  paymentMode: PaymentMode; taxType?: TaxType | null; billType?: BillType | null; salespersonId?: number | null; salespersonName?: string;
  priceListId?: number | null; remarks?: string; referenceNo?: string; referenceDate?: string | null; orderDetails?: string; despatchDetails?: string;
  deliveryDate?: string | null; sourceOrderId?: number | null;
  billDiscountPercent: number; billDiscount: number; otherExpense: number; otherExpenseTaxRate: number; freight: number; freightTaxRate: number;
  roundOffMode?: RoundOffMode | null; cashDiscountPercent: number; cashDiscount: number; costFactor: number;
  cashAmount: number; cardAmount: number; creditAmount: number; tenderedAmount: number; cardLedgerId?: number | null; cardLast4?: string | null;
  paymentReference?: string; pointsRedeemed: number; bomId?: number | null; labourCost: number; otherCost: number;
  grossAmount: number; itemDiscount: number; taxableAmount: number; exemptAmount: number; cgstAmount: number; sgstAmount: number;
  igstAmount: number; cessAmount: number; addCessAmount: number; kfCessAmount: number; serviceTaxAmount: number; serviceCessAmount: number;
  otherExpenseTax: number; freightTax: number; roundOff: number; billAmount: number; totalMrp: number; cost: number; profit: number;
  pointsEarned: number; redeemAmount: number; cancelled?: boolean; cancelReason?: string; isProcessed?: boolean; counter?: string;
  userName?: string; createdAt?: string; warnings?: string[];
  lines: TradeLine[];
}

export interface DocumentListItem {
  id: number; voucherType: VoucherType; documentNo: string; date: string; partyName?: string; paymentMode: PaymentMode;
  taxableAmount: number; taxAmount: number; billAmount: number; cancelled: boolean; isProcessed: boolean; userName?: string;
  counter?: string; referenceNo?: string;
}

export interface ProductLookup {
  productId: number; code: string; name: string; regionalName?: string; hsnCode?: string; categoryId?: number; itemClass: ItemClass;
  mainUnit: string; altUnit?: string | null; conversionFactor: number; cgstRate: number; sgstRate: number; igstRate: number;
  hasSecondaryTax: boolean; secondaryTaxThreshold: number; cgst2Rate: number; sgst2Rate: number; igst2Rate: number;
  cessRate: number; addCessPerUnit: number; kfCessRate: number; saleTaxInclusive: boolean; purchaseTaxInclusive: boolean;
  purchaseRate: number; saleRate: number; mrp: number; cost: number; discountPercent: number; trackExpiry: boolean; isScaleItem: boolean;
  piecesPerBox: number; sqftPerBox: number; batchId?: number | null; batchCode?: string | null; expiryDate?: string | null; stock: number;
  price: number; priceSource: string; scaleQty?: number | null; hasImage: boolean;
}

export interface PartyLookup {
  id: number; code: string; name: string; mobile?: string; address?: string; gstin?: string; stateCode?: string; priceListId?: number | null;
  loyaltySchemeId?: number | null; discountPercent: number; creditLimit: number; salespersonId?: number | null; balance: number;
  loyaltyPoints: number; loyaltyTarget: number; isWalkIn: boolean;
}

export interface VoucherLine {
  id?: number; lineNo?: number; ledgerId: number; ledgerName?: string; side: EntrySide; amount: number; narration?: string;
  allocations: { billId: number; amount: number; documentNo?: string }[];
}

export interface Voucher {
  id: number; voucherType: VoucherType; documentNo?: string; date: string; version?: number; rowVersion?: string | null;
  headerLedgerId?: number | null; headerLedgerName?: string; headerSide: EntrySide; total: number; narration?: string; referenceNo?: string;
  chequeNo?: string; chequeDate?: string | null; bank?: string; chequeStatus?: string; isReconciled?: boolean; reconciledOn?: string | null;
  salespersonId?: number | null; cancelled?: boolean; userName?: string; lines: VoucherLine[];
}

export interface OpenBill {
  id: number; voucherType: VoucherType; documentNo: string; date: string; dueDate?: string; side: EntrySide; amount: number; settled: number;
  balance: number; ageDays: number;
}

export interface ReportColumn { key: string; title: string; type: 'text' | 'number' | 'money' | 'qty' | 'date' | 'percent'; total: boolean; }
export interface ReportResult {
  key: string; title: string; subtitle?: string; columns: ReportColumn[]; rows: Record<string, any>[]; totals?: Record<string, any> | null;
  summary: { key: string; value: string }[]; landscape: boolean;
}
export interface ReportDefinition { key: string; title: string; group: string; screen: string; filters: string[]; description?: string; }

export interface LookupItem { id: number; name: string; isActive: boolean; showInPos?: boolean; sortOrder?: number; color?: string | null; usageCount?: number; }

export interface Employee {
  id: number; code: string; name: string; ledgerId?: number; address?: string; phone?: string; gender?: string; bloodGroup?: string;
  shiftStart?: string | null; shiftEnd?: string | null; salary: number; commissionPercent: number; category?: string; isSalesperson: boolean; isActive: boolean;
}

export const VOUCHER_LABELS: Record<VoucherType, string> = {
  Sales: 'Sales', LocalSales: 'Local Sales', Estimate: 'Estimate', SalesOrder: 'Sales Order', SalesReturn: 'Sales Return',
  DeliveryNote: 'Delivery Note', Purchase: 'Purchase', LocalPurchase: 'Local Purchase', ReceiptNote: 'Receipt Note',
  PurchaseReturn: 'Purchase Return', Receipt: 'Receipt', Payment: 'Payment', Journal: 'Journal', Contra: 'Contra',
  DebitNote: 'Debit Note', CreditNote: 'Credit Note', OpeningStock: 'Opening Stock', StockAdjustment: 'Stock Adjustment',
  Production: 'Production', PurchaseOrder: 'Purchase Order', POSSales: 'POS Sales', Opening: 'Opening Balance',
};

export const SCREEN_FOR: Partial<Record<VoucherType, string>> = {
  Sales: 'Sales', LocalSales: 'Sales', POSSales: 'POS', SalesReturn: 'SalesReturn', Estimate: 'Estimate', SalesOrder: 'SalesOrder',
  DeliveryNote: 'DeliveryNote', Purchase: 'Purchase', LocalPurchase: 'Purchase', PurchaseReturn: 'PurchaseReturn',
  PurchaseOrder: 'PurchaseOrder', ReceiptNote: 'ReceiptNote', OpeningStock: 'OpeningStock', StockAdjustment: 'StockAdjustment',
  Production: 'Production', Opening: 'OpeningBalance', Receipt: 'Voucher', Payment: 'Voucher', Journal: 'Voucher', Contra: 'Voucher',
  DebitNote: 'Voucher', CreditNote: 'Voucher',
};

export const PURCHASE_SIDE: VoucherType[] = ['Purchase', 'LocalPurchase', 'PurchaseReturn', 'PurchaseOrder', 'ReceiptNote', 'OpeningStock'];
export const LANDED_COST: VoucherType[] = ['Purchase', 'LocalPurchase', 'ReceiptNote', 'OpeningStock', 'PurchaseOrder'];
export const TAX_FREE: VoucherType[] = ['OpeningStock', 'StockAdjustment', 'Production'];
export const POSTS_ACCOUNTS: VoucherType[] = ['Sales', 'LocalSales', 'POSSales', 'SalesReturn', 'Purchase', 'LocalPurchase', 'PurchaseReturn'];
export const STATES: Record<string, string> = {};
