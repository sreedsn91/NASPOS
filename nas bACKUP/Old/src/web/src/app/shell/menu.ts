/** screen '' = personal page every signed-in user may open. */
export interface MenuItem { label: string; link: string; screen: string; right?: 'Read' | 'Add'; key?: string; query?: Record<string, string>; }
export interface MenuGroup { label: string; icon: string; items: MenuItem[]; }

export const MENU: MenuGroup[] = [
  { label: 'Home', icon: 'home', items: [{ label: 'Dashboard', link: '/dashboard', screen: 'Dashboard' }] },
  {
    label: 'Sales', icon: 'cart', items: [
      { label: 'Touch POS', link: '/pos', screen: 'POS', right: 'Add', key: 'F9' },
      { label: 'Sales Invoice', link: '/doc/Sales/new', screen: 'Sales', right: 'Add' },
      { label: 'Local Sales', link: '/doc/LocalSales/new', screen: 'Sales', right: 'Add' },
      { label: 'Sales Return', link: '/doc/SalesReturn/new', screen: 'SalesReturn', right: 'Add' },
      { label: 'Estimate', link: '/doc/Estimate/new', screen: 'Estimate', right: 'Add' },
      { label: 'Sales Order', link: '/doc/SalesOrder/new', screen: 'SalesOrder', right: 'Add' },
      { label: 'Delivery Note', link: '/doc/DeliveryNote/new', screen: 'DeliveryNote', right: 'Add' },
      { label: 'Search Bills', link: '/documents/Sales', screen: 'Sales' },
    ],
  },
  {
    label: 'Purchase', icon: 'truck', items: [
      { label: 'Purchase', link: '/doc/Purchase/new', screen: 'Purchase', right: 'Add' },
      { label: 'Local Purchase', link: '/doc/LocalPurchase/new', screen: 'Purchase', right: 'Add' },
      { label: 'Purchase Return', link: '/doc/PurchaseReturn/new', screen: 'PurchaseReturn', right: 'Add' },
      { label: 'Purchase Order', link: '/doc/PurchaseOrder/new', screen: 'PurchaseOrder', right: 'Add' },
      { label: 'Receipt Note', link: '/doc/ReceiptNote/new', screen: 'ReceiptNote', right: 'Add' },
      { label: 'Search Purchases', link: '/documents/Purchase', screen: 'Purchase' },
    ],
  },
  {
    label: 'Inventory', icon: 'box', items: [
      { label: 'Products', link: '/masters/products', screen: 'Product' },
      { label: 'Opening Stock', link: '/doc/OpeningStock/new', screen: 'OpeningStock', right: 'Add' },
      { label: 'Stock Adjustment', link: '/doc/StockAdjustment/new', screen: 'StockAdjustment', right: 'Add' },
      { label: 'Production', link: '/doc/Production/new', screen: 'Production', right: 'Add' },
      { label: 'Bill of Materials', link: '/masters/boms', screen: 'Bom' },
      { label: 'Barcode Labels', link: '/inventory/labels', screen: 'Printing' },
      { label: 'Stock History', link: '/reports/stock-history', screen: 'StockReports' },
      { label: 'Stock Summary', link: '/reports/stock-summary', screen: 'StockReports' },
    ],
  },
  {
    label: 'Accounts', icon: 'wallet', items: [
      { label: 'Receipt', link: '/voucher/Receipt/new', screen: 'Voucher', right: 'Add' },
      { label: 'Payment', link: '/voucher/Payment/new', screen: 'Voucher', right: 'Add' },
      { label: 'Journal', link: '/voucher/Journal/new', screen: 'Voucher', right: 'Add' },
      { label: 'Contra', link: '/voucher/Contra/new', screen: 'Voucher', right: 'Add' },
      { label: 'Debit Note', link: '/voucher/DebitNote/new', screen: 'Voucher', right: 'Add' },
      { label: 'Credit Note', link: '/voucher/CreditNote/new', screen: 'Voucher', right: 'Add' },
      { label: 'Voucher Search', link: '/vouchers', screen: 'Voucher' },
      { label: 'Opening Balances', link: '/accounts/opening', screen: 'OpeningBalance' },
      { label: 'Cheque Register', link: '/accounts/cheques', screen: 'Voucher' },
      { label: 'Day Book', link: '/reports/day-book', screen: 'AccountReports' },
      { label: 'Ledger Statement', link: '/reports/ledger-statement', screen: 'AccountReports' },
      { label: 'Trial Balance', link: '/reports/trial-balance', screen: 'AccountReports' },
      { label: 'Profit & Loss', link: '/reports/profit-loss', screen: 'AccountReports' },
      { label: 'Balance Sheet', link: '/reports/balance-sheet', screen: 'AccountReports' },
    ],
  },
  {
    label: 'Masters', icon: 'users', items: [
      { label: 'Customers', link: '/masters/parties/Customer', screen: 'Customer' },
      { label: 'Suppliers', link: '/masters/parties/Supplier', screen: 'Supplier' },
      { label: 'Ledgers', link: '/masters/parties/None', screen: 'Ledger' },
      { label: 'Account Groups', link: '/masters/account-groups', screen: 'AccountGroup' },
      { label: 'Employees', link: '/masters/employees', screen: 'Employee' },
      { label: 'Categories', link: '/masters/lookups/category', screen: 'Masters' },
      { label: 'Manufacturers', link: '/masters/lookups/manufacturer', screen: 'Masters' },
      { label: 'Areas', link: '/masters/lookups/area', screen: 'Masters' },
      { label: 'Price Lists', link: '/masters/price-lists', screen: 'PriceList' },
      { label: 'Loyalty Schemes', link: '/masters/loyalty', screen: 'Loyalty' },
      { label: 'Item Offers', link: '/masters/offers', screen: 'Offer' },
    ],
  },
  {
    label: 'Reports', icon: 'chart', items: [
      { label: 'All Reports', link: '/reports', screen: 'Dashboard' },
      { label: 'GST / GSTR-1', link: '/reports/gstr1-b2b', screen: 'TaxReports' },
      { label: 'Outstanding Ageing', link: '/reports/ageing', screen: 'AccountReports' },
      { label: 'End of Day', link: '/reports/end-of-day', screen: 'SalesReports' },
    ],
  },
  {
    label: 'Settings', icon: 'settings', items: [
      { label: 'Appearance (theme)', link: '/settings/appearance', screen: '' },
      { label: 'Company Profile', link: '/settings/company', screen: 'Company' },
      { label: 'Features', link: '/settings/features', screen: 'Settings' },
      { label: 'Screen Options', link: '/settings/screens', screen: 'Settings' },
      { label: 'Numbering', link: '/settings/numbering', screen: 'Settings' },
      { label: 'Print Schemes', link: '/settings/printing', screen: 'Printing' },
      { label: 'Messaging (SMS/E-mail)', link: '/settings/messaging', screen: 'Messaging' },
      { label: 'Users', link: '/settings/users', screen: 'Users' },
      { label: 'Roles & Rights', link: '/settings/roles', screen: 'Roles' },
      { label: 'User Log', link: '/settings/audit', screen: 'UserLog' },
      { label: 'Companies & Backup', link: '/settings/companies', screen: 'Company' },
      { label: 'Year End Closing', link: '/settings/year-end', screen: 'YearEnd' },
      { label: 'Import / Export', link: '/tools/import-export', screen: 'Tools' },
      { label: 'Copy to Company', link: '/tools/transfer', screen: 'Tools' },
      { label: 'Reset Data', link: '/tools/reset', screen: 'Tools' },
    ],
  },
];
