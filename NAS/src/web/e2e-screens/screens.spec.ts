import { APIRequestContext, Page, expect, request, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Seeds a demo shop through the API, then captures every screen into docs/screens/<nn>-<name>.jpg for the user guide.
 * A screen that fails is logged and skipped so one problem doesn't lose the rest.
 */
const OUT = join(__dirname, '..', '..', '..', 'docs', 'screens');
const PASSWORD = 'Demo@12345';
const BASE = 'http://localhost:5080';

/** GSTIN check character (same algorithm as the server). */
function gst(first14: string): string {
  const chars = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ';
  let sum = 0;
  for (let i = 0; i < 14; i++) { const p = chars.indexOf(first14[i]) * (i % 2 === 0 ? 1 : 2); sum += Math.floor(p / 36) + (p % 36); }
  return first14 + chars[(36 - (sum % 36)) % 36];
}
/** EAN-13 with a valid check digit. */
function ean(body12: string): string {
  let sum = 0;
  for (let i = 0; i < 12; i++) sum += +body12[11 - i] * (i % 2 === 0 ? 3 : 1);
  return body12 + ((10 - (sum % 10)) % 10);
}
const day = (iso: string) => new Date(iso + 'T11:30:00+05:30').toISOString();

let api: APIRequestContext;
async function call<T = any>(method: 'get' | 'post' | 'put', url: string, data?: unknown): Promise<T> {
  const r = await api[method]('/api' + url, data === undefined ? {} : { data });
  if (!r.ok()) throw new Error(`${method.toUpperCase()} ${url} -> ${r.status()} ${await r.text()}`);
  const t = await r.text();
  return (t ? JSON.parse(t) : undefined) as T;
}
const id = (x: { id: number }) => x.id;

const ids: Record<string, number> = {};

test.describe.serial('screen guide screenshots', () => {
  let page: Page;
  let n = 0;
  const failures: string[] = [];

  async function shot(name: string, prepare?: () => Promise<void>): Promise<void> {
    const file = `${String(++n).padStart(2, '0')}-${name}.jpg`;
    try {
      if (prepare) await prepare();
      await page.waitForLoadState('networkidle').catch(() => undefined);
      await page.waitForTimeout(500);
      await page.screenshot({ path: join(OUT, file), type: 'jpeg', quality: 82 });
      console.log('captured', file);
    } catch (e) {
      failures.push(`${file}: ${(e as Error).message.split('\n')[0]}`);
      console.log('FAILED', file, (e as Error).message.split('\n')[0]);
    }
  }
  const go = (url: string) => async () => { await page.goto(url); };
  const pick = async (placeholderStart: string, text: string) => {
    await page.locator(`app-lookup input[placeholder^="${placeholderStart}"]`).first().fill(text);
    const opt = page.locator('.lookup-panel .opt').first();
    await expect(opt).toBeVisible({ timeout: 10_000 });
    await opt.click();
  };

  test.beforeAll(async ({ browser }) => {
    mkdirSync(OUT, { recursive: true });
    page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  });
  test.afterAll(async () => {
    await page.close();
    if (failures.length) console.log('\nScreens that failed:\n' + failures.join('\n'));
  });

  test('first-run screens, then demo data', async () => {
    await shot('setup', async () => {
      await page.goto('/login');
      await expect(page).toHaveURL(/\/setup/);
      await page.locator('input[name="cn"]').fill('Sree Traders');
      await page.locator('input[name="g"]').fill(gst('32AAKFS1234M1Z'));
      await page.locator('input[name="a"]').fill('MG Road, Ernakulam');
      await page.locator('input[name="ci"]').fill('Kochi');
      await page.locator('input[name="ph"]').fill('0484 2345678');
    });

    // --- Seed through the API (same server, same database) ---
    const anon = await request.newContext({ baseURL: BASE, timeout: 300_000 });
    const setup = await (await anon.post('/api/auth/setup', { data: {
      companyName: 'Sree Traders', stateCode: '32', gstin: gst('32AAKFS1234M1Z'), address: 'MG Road, Ernakulam', city: 'Kochi', phone: '0484 2345678',
      adminUserName: 'admin', adminDisplayName: 'Sreekumar', adminPassword: PASSWORD, financialYearStart: '2026-04-01',
    } })).json();
    await anon.dispose();
    api = await request.newContext({ baseURL: BASE, timeout: 300_000, extraHTTPHeaders: { Authorization: `Bearer ${setup.accessToken}`, 'X-Counter': 'COUNTER-1' } });

    await call('put', '/company/settings', {
      EnableExpiry: 'true', EnableProduction: 'true', EnablePriceList: 'true', EnableItemOffers: 'true', EnableLoyalty: 'true',
      EnableUnitConversion: 'true', EnableCess: 'true', NegativeStockMode: 'Warn',
    });
    const cat = async (name: string, color: string, sortOrder: number) =>
      (await call('post', '/lookups/category', { id: 0, name, isActive: true, showInPos: true, sortOrder, color })).id as number;
    const c = { grocery: await cat('Grocery', '#2f855a', 1), dairy: await cat('Dairy', '#2b6cb0', 2), personal: await cat('Personal Care', '#b83280', 3), drinks: await cat('Beverages', '#c05621', 4), bakery: await cat('Bakery', '#975a16', 5) };
    const mf = async (name: string) => (await call('post', '/lookups/manufacturer', { id: 0, name, isActive: true })).id as number;
    const m = { milma: await mf('Milma'), hul: await mf('Hindustan Unilever'), itc: await mf('ITC'), local: await mf('Local Farms') };
    const area = async (name: string) => (await call('post', '/lookups/area', { id: 0, name, isActive: true })).id as number;
    const ar = { kaloor: await area('Kaloor'), edappally: await area('Edappally') };
    await area('Vyttila');

    const emp = async (name: string, commissionPercent: number) => id(await call('post', '/employees', { name, commissionPercent, isSalesperson: true, isActive: true, salary: 15000, phone: '98470' + Math.floor(10000 + Math.random() * 89999) }));
    ids.ravi = await emp('Ravi', 1.5);
    ids.anu = await emp('Anu', 1);

    const groups: any[] = await call('get', '/account-groups');
    const g = (code: string) => groups.find(x => x.systemCode === code)?.id;

    const loyalty = id(await call('post', '/loyalty-schemes', { name: 'Gold Card', saleAmount: 100, points: 1, targetPoints: 50, pointValue: 1, isActive: true }));
    const wholesale = id(await call('post', '/lookups/pricelist', { id: 0, name: 'Wholesale', isActive: true }));

    const ledger = async (body: any) => id(await call('post', '/ledgers', { isActive: true, ...body }));
    ids.anil = await ledger({ name: 'Anil Kumar', partyType: 'Customer', accountGroupId: g('SundryDebtors'), mobile: '9847012345', city: 'Kochi', stateCode: '32', areaId: ar.kaloor, loyaltySchemeId: loyalty, creditLimit: 25000, creditDays: 15 });
    ids.meera = await ledger({ name: 'Meera Stores', partyType: 'Customer', isCorporate: true, accountGroupId: g('SundryDebtors'), mobile: '9846098765', gstin: gst('32ABCPM4321K1Z'), stateCode: '32', city: 'Aluva', areaId: ar.edappally, priceListId: wholesale, creditLimit: 100000, creditDays: 30, openingBalance: 5400, openingSide: 'Dr' });
    ids.kanara = await ledger({ name: 'Kanara Retail', partyType: 'Customer', isCorporate: true, accountGroupId: g('SundryDebtors'), mobile: '9900112233', gstin: gst('29AAGCK5678L1Z'), stateCode: '29', city: 'Mangaluru', creditLimit: 50000 });
    ids.priya = await ledger({ name: 'Priya Nair', partyType: 'Customer', accountGroupId: g('SundryDebtors'), mobile: '9895011122', stateCode: '32', city: 'Kochi', loyaltySchemeId: loyalty });
    ids.metro = await ledger({ name: 'Metro Distributors', partyType: 'Supplier', isCorporate: true, accountGroupId: g('SundryCreditors'), mobile: '9447055566', gstin: gst('32AAFFM9876Q1Z'), stateCode: '32', city: 'Kochi', creditDays: 21, openingBalance: 12000, openingSide: 'Cr' });
    ids.sunrise = await ledger({ name: 'Sunrise FMCG', partyType: 'Supplier', isCorporate: true, accountGroupId: g('SundryCreditors'), mobile: '9845077788', gstin: gst('33AABCS1111H1Z'), stateCode: '33', city: 'Coimbatore' });
    ids.rent = await ledger({ name: 'Shop Rent', partyType: 'None', accountGroupId: g('IndirectExpense') });
    ids.power = await ledger({ name: 'Electricity', partyType: 'None', accountGroupId: g('IndirectExpense') });
    ids.bank = await ledger({ name: 'Federal Bank', partyType: 'None', accountGroupId: g('BankAccounts') ?? g('Bank'), isCardLedger: true }).catch(() => 0);
    const cashLedger = (await call('get', '/ledgers?search=CASH&pageSize=5')).items.find((l: any) => l.code === 'CASH').id;

    let barcodeSeq = 890100000001;
    const rateOf: Record<number, number> = {};
    const prod = async (b: any) => {
      const pid = id(await call('post', '/products', { mainUnit: 'NOS', conversionFactor: 1, isActive: true, itemClass: 'Goods', defaultBarcode: ean(String(barcodeSeq++)), ...b }));
      rateOf[pid] = b.saleRate;
      return pid;
    };
    const P: Record<string, number> = {};
    P.rice = await prod({ name: 'Rose Matta Rice 5kg', hsnCode: '1006', categoryId: c.grocery, manufacturerId: m.local, gstRate: 5, purchaseRate: 260, saleRate: 299, mrp: 320, reorderLevel: 10, rack: 'A1' });
    P.atta = await prod({ name: 'Whole Wheat Atta 1kg', hsnCode: '1101', categoryId: c.grocery, manufacturerId: m.itc, gstRate: 5, purchaseRate: 48, saleRate: 58, mrp: 62, reorderLevel: 20 });
    P.oil = await prod({ name: 'Coconut Oil 1L', hsnCode: '1513', categoryId: c.grocery, manufacturerId: m.local, gstRate: 5, purchaseRate: 180, saleRate: 215, mrp: 230, reorderLevel: 12 });
    P.sugar = await prod({ name: 'Sugar 1kg', hsnCode: '1701', categoryId: c.grocery, gstRate: 5, purchaseRate: 40, saleRate: 46, mrp: 48, reorderLevel: 25 });
    P.milk = await prod({ name: 'Milma Toned Milk 500ml', hsnCode: '0401', categoryId: c.dairy, manufacturerId: m.milma, gstRate: 0, purchaseRate: 23, saleRate: 26, mrp: 26, trackExpiry: true, reorderLevel: 30 });
    P.curd = await prod({ name: 'Milma Curd 400g', hsnCode: '0403', categoryId: c.dairy, manufacturerId: m.milma, gstRate: 5, purchaseRate: 30, saleRate: 35, mrp: 36, trackExpiry: true });
    P.ghee = await prod({ name: 'Milma Ghee 200ml', hsnCode: '0405', categoryId: c.dairy, manufacturerId: m.milma, gstRate: 12, purchaseRate: 120, saleRate: 140, mrp: 145, trackExpiry: true });
    P.soap = await prod({ name: 'Lux Soap 100g', hsnCode: '3401', categoryId: c.personal, manufacturerId: m.hul, gstRate: 18, purchaseRate: 30, saleRate: 38, mrp: 42, altUnit: 'BOX', conversionFactor: 12 });
    P.paste = await prod({ name: 'Toothpaste 150g', hsnCode: '3306', categoryId: c.personal, manufacturerId: m.hul, gstRate: 18, purchaseRate: 78, saleRate: 95, mrp: 99 });
    P.shampoo = await prod({ name: 'Shampoo 340ml', hsnCode: '3305', categoryId: c.personal, manufacturerId: m.hul, gstRate: 18, purchaseRate: 260, saleRate: 315, mrp: 340, saleTaxInclusive: true });
    P.tea = await prod({ name: 'Tea Powder 250g', hsnCode: '0902', categoryId: c.drinks, manufacturerId: m.itc, gstRate: 5, purchaseRate: 105, saleRate: 125, mrp: 130 });
    P.cola = await prod({ name: 'Cola 750ml', hsnCode: '2202', categoryId: c.drinks, gstRate: 28, cessRate: 12, purchaseRate: 30, saleRate: 38, mrp: 40, reorderLevel: 24 });
    P.bread = await prod({ name: 'Sandwich Bread 400g', hsnCode: '1905', categoryId: c.bakery, gstRate: 0, purchaseRate: 32, saleRate: 40, mrp: 40, trackExpiry: true });
    P.cake = await prod({ name: 'Plum Cake 500g', hsnCode: '1905', categoryId: c.bakery, gstRate: 18, purchaseRate: 150, saleRate: 190, mrp: 200, defaultBarcode: null });
    P.flour = await prod({ name: 'Maida 1kg', hsnCode: '1101', categoryId: c.bakery, gstRate: 5, purchaseRate: 38, saleRate: 45, mrp: 48, defaultBarcode: null });
    P.delivery = await prod({ name: 'Home Delivery', hsnCode: '9968', itemClass: 'Service', gstRate: 18, saleRate: 30, defaultBarcode: null });

    // Product pictures for POS tiles: a coloured card with the item initials, rendered by the browser.
    const art = page; // the setup screen is already captured; the page is reused for drawing, then navigates on
    const pics: [number, string, string][] = [[P.rice, 'RICE', '#2f855a'], [P.oil, 'OIL', '#b7791f'], [P.milk, 'MILK', '#2b6cb0'], [P.ghee, 'GHEE', '#d69e2e'],
      [P.soap, 'SOAP', '#b83280'], [P.paste, 'PASTE', '#319795'], [P.tea, 'TEA', '#9c4221'], [P.cola, 'COLA', '#c53030'], [P.bread, 'BREAD', '#975a16'], [P.cake, 'CAKE', '#6b46c1']];
    for (const [pid, label, color] of pics) {
      await art.setContent(`<div id="x" style="width:240px;height:240px;display:flex;align-items:center;justify-content:center;background:linear-gradient(135deg,${color},#1a202c);color:#fff;font:700 44px Segoe UI,Arial;border-radius:24px">${label}</div>`);
      const buffer = await art.locator('#x').screenshot({ type: 'png' });
      await api.put(`/api/products/${pid}/image`, { multipart: { file: { name: `${label}.png`, mimeType: 'image/png', buffer } } });
    }

    const priceRows: any[] = await call('get', `/price-lists/${wholesale}/items`);
    await call('put', `/price-lists/${wholesale}/items`, priceRows.map(r => ({ productId: r.productId, price: Math.round(r.saleRate * 0.94 * 100) / 100 })));
    await call('post', '/offers/fill', { name: 'Onam Offer', startAt: day('2026-09-25'), endAt: day('2026-10-15'), categoryId: c.drinks, discountPercent: 10, discountAmount: 0 });
    await call('post', '/boms', { name: 'Plum Cake (batch of 10)', batchSize: 10, isActive: true, finishedProductId: P.cake,
      lines: [{ productId: P.cake, kind: 'FinishedGood', qty: 10, isAltUnit: false }, { productId: P.flour, kind: 'RawMaterial', qty: 3, isAltUnit: false },
        { productId: P.sugar, kind: 'RawMaterial', qty: 2, isAltUnit: false }, { productId: P.ghee, kind: 'RawMaterial', qty: 1, isAltUnit: false }] });

    const doc = async (body: any) => call('post', '/documents', { acknowledgeWarnings: true, ...body });
    const exp = (d: string) => d;
    // Opening stock + purchases (intra-state and inter-state)
    ids.opening = id(await doc({ voucherType: 'OpeningStock', date: day('2026-04-01'), taxType: 'NoTax', remarks: 'Stock as on 1 April',
      lines: [{ productId: P.rice, qty: 40, rate: 255 }, { productId: P.atta, qty: 60, rate: 47 }, { productId: P.sugar, qty: 80, rate: 39 }, { productId: P.flour, qty: 50, rate: 37 }] }));
    ids.purchase1 = id(await doc({ voucherType: 'Purchase', date: day('2026-04-05'), paymentMode: 'Credit', partyLedgerId: ids.metro, referenceNo: 'MD/2241', referenceDate: '2026-04-04',
      lines: [{ productId: P.oil, qty: 48, rate: 180, mrp: 230, saleRate: 215 }, { productId: P.milk, qty: 200, rate: 23, mrp: 26, saleRate: 26, expiryDate: exp('2026-10-20') },
        { productId: P.curd, qty: 60, rate: 30, mrp: 36, saleRate: 35, expiryDate: exp('2026-10-12') }, { productId: P.ghee, qty: 30, rate: 120, mrp: 145, saleRate: 140, expiryDate: exp('2027-03-31') },
        { productId: P.bread, qty: 80, rate: 32, mrp: 40, saleRate: 40, expiryDate: exp('2026-10-06') }, { productId: P.tea, qty: 40, rate: 105, mrp: 130, saleRate: 125 }], freight: 250 }));
    ids.purchase2 = id(await doc({ voucherType: 'Purchase', date: day('2026-04-08'), paymentMode: 'Credit', partyLedgerId: ids.sunrise, referenceNo: 'SF-0917', referenceDate: '2026-04-07',
      lines: [{ productId: P.soap, qty: 240, rate: 30, mrp: 42, saleRate: 38 }, { productId: P.paste, qty: 72, rate: 78, mrp: 99, saleRate: 95 },
        { productId: P.shampoo, qty: 36, rate: 260, mrp: 340, saleRate: 315 }, { productId: P.cola, qty: 120, rate: 30, mrp: 40, saleRate: 38 }] }));
    await doc({ voucherType: 'Purchase', date: day('2026-07-10'), paymentMode: 'Cash', partyLedgerId: ids.metro, referenceNo: 'MD/2655',
      lines: [{ productId: P.rice, qty: 30, rate: 262, mrp: 320, saleRate: 299 }, { productId: P.oil, qty: 24, rate: 182, mrp: 230, saleRate: 215 }] });
    await doc({ voucherType: 'PurchaseReturn', date: day('2026-04-20'), paymentMode: 'Credit', partyLedgerId: ids.sunrise, remarks: 'Damaged cartons', lines: [{ productId: P.cola, qty: 6, rate: 30 }] });
    await doc({ voucherType: 'PurchaseOrder', date: day('2026-09-28'), partyLedgerId: ids.metro, deliveryDate: '2026-10-05', lines: [{ productId: P.milk, qty: 150, rate: 23 }, { productId: P.bread, qty: 60, rate: 32 }] });

    // Sales spread over the year, so the dashboard chart and reports have shape.
    const sale = (type: string, date: string, mode: string, party: number | null, lines: [number, number][], extra: any = {}) => doc({
      voucherType: type, date: day(date), paymentMode: mode, partyLedgerId: party, salespersonId: extra.sp ?? ids.ravi,
      lines: lines.map(([productId, qty]) => ({ productId, qty, rate: rateOf[productId] })), ...extra,
    });
    const months = ['2026-04-12', '2026-05-09', '2026-05-24', '2026-06-14', '2026-07-03', '2026-07-21', '2026-08-11', '2026-08-29', '2026-09-08', '2026-09-22', '2026-10-01'];
    for (const [i, d] of months.entries()) {
      await sale('POSSales', d, 'Cash', null, [[P.milk, 4 + (i % 3)], [P.bread, 2], [P.tea, 1], [P.soap, 1]], { sp: i % 2 ? ids.anu : ids.ravi, tenderedAmount: 1000 });
      await sale('Sales', d, i % 3 === 0 ? 'Credit' : 'Cash', i % 3 === 0 ? ids.anil : ids.priya, [[P.rice, 2], [P.oil, 1 + (i % 2)], [P.atta, 3], [P.paste, 2]], { sp: i % 2 ? ids.anu : ids.ravi });
    }
    ids.b2b = id(await sale('Sales', '2026-09-30', 'Credit', ids.meera, [[P.rice, 10], [P.atta, 20], [P.sugar, 25], [P.oil, 12]], { priceListId: wholesale, billType: 'B2B', orderDetails: 'PO 1187', despatchDetails: 'Own van' }));
    ids.igst = id(await sale('Sales', '2026-09-26', 'Credit', ids.kanara, [[P.shampoo, 6], [P.soap, 10], [P.cola, 24]]));
    // Today's counter sales (earlier today), so the dashboard's "today" tiles and counters have figures.
    const now = Date.now();
    for (const [i, hoursAgo] of [5, 4, 3, 2, 1].entries()) {
      await call('post', '/documents', { acknowledgeWarnings: true, voucherType: 'POSSales', date: new Date(now - hoursAgo * 3_600_000).toISOString(),
        paymentMode: 'Cash', salespersonId: i % 2 ? ids.anu : ids.ravi, tenderedAmount: 500,
        lines: [{ productId: P.milk, qty: 2 + i, rate: rateOf[P.milk] }, { productId: P.cola, qty: 2, rate: rateOf[P.cola] }, { productId: P.bread, qty: 1, rate: rateOf[P.bread] }] })
        .catch(e => console.log('pos today', (e as Error).message));
    }
    await sale('SalesReturn', '2026-09-23', 'Cash', ids.priya, [[P.paste, 1]], { remarks: 'Wrong flavour' });
    ids.estimate = id(await sale('Estimate', '2026-09-29', 'Cash', ids.meera, [[P.ghee, 12], [P.tea, 10]]));
    ids.order = id(await sale('SalesOrder', '2026-09-30', 'Credit', ids.kanara, [[P.shampoo, 12], [P.paste, 24]], { deliveryDate: '2026-10-06' }));
    await sale('DeliveryNote', '2026-10-01', 'Credit', ids.meera, [[P.sugar, 10]]);
    await doc({ voucherType: 'StockAdjustment', date: day('2026-09-15'), taxType: 'NoTax', remarks: 'Damaged in storage', lines: [{ productId: P.bread, qty: 3, rate: 32, direction: 'Out' }, { productId: P.cola, qty: 2, rate: 30, direction: 'Out' }] });

    // Accounts
    const bills: any[] = await call('get', `/vouchers/open-bills/${ids.anil}`);
    ids.receipt = id(await call('post', '/vouchers', { voucherType: 'Receipt', date: day('2026-09-20'), headerLedgerId: cashLedger, narration: 'Part payment',
      lines: [{ ledgerId: ids.anil, amount: 1500, allocations: bills.filter(b => b.side === 'Dr').slice(0, 1).map(b => ({ billId: b.id, amount: Math.min(1500, b.balance) })) }] }));
    if (ids.bank) await call('post', '/vouchers', { voucherType: 'Receipt', date: day('2026-09-27'), headerLedgerId: ids.bank, narration: 'Cheque from Meera Stores', chequeNo: '004512', chequeDate: '2026-09-27', bank: 'SBI Aluva', lines: [{ ledgerId: ids.meera, amount: 5400, allocations: [] }] });
    await call('post', '/vouchers', { voucherType: 'Payment', date: day('2026-09-05'), headerLedgerId: cashLedger, headerSide: 'Cr', narration: 'September rent', lines: [{ ledgerId: ids.rent, amount: 18000, allocations: [] }] });
    await call('post', '/vouchers', { voucherType: 'Payment', date: day('2026-09-12'), headerLedgerId: cashLedger, headerSide: 'Cr', narration: 'KSEB bill', lines: [{ ledgerId: ids.power, amount: 3240, allocations: [] }] });
    await call('post', '/vouchers', { voucherType: 'Payment', date: day('2026-09-18'), headerLedgerId: ids.bank || cashLedger, headerSide: 'Cr', narration: 'Metro invoice MD/2241', chequeNo: '118804', chequeDate: '2026-09-18', bank: 'Federal Bank', lines: [{ ledgerId: ids.metro, amount: 15000, allocations: [] }] });
    if (ids.bank) await call('post', '/vouchers', { voucherType: 'Contra', date: day('2026-09-30'), headerLedgerId: ids.bank, headerSide: 'Dr', narration: 'Cash deposited', lines: [{ ledgerId: cashLedger, amount: 25000, allocations: [] }] });
    ids.journal = id(await call('post', '/vouchers', { voucherType: 'Journal', date: day('2026-09-30'), narration: 'Rent provision for October',
      lines: [{ ledgerId: ids.rent, side: 'Dr', amount: 18000, allocations: [] }, { ledgerId: ids.metro, side: 'Cr', amount: 18000, allocations: [] }] }));

    // Extra users, roles and message templates so those screens aren't empty.
    const screens: string[] = await call('get', '/roles/screens');
    const cashierRole = id(await call('post', '/roles', { name: 'Cashier', isActive: true, permissions: screens.map(s => ({ screen: s, rights: ['POS', 'Sales', 'SalesReturn', 'Customer', 'Dashboard'].includes(s) ? 3 | 32 : 0 })) }));
    await call('post', '/roles', { name: 'Accountant', isActive: true, permissions: screens.map(s => ({ screen: s, rights: /Voucher|Account|Ledger|Report|Opening/.test(s) ? 511 : 1 })) });
    await call('post', '/users', { userName: 'cashier1', displayName: 'Counter 1 cashier', isActive: true, roleId: cashierRole, password: 'Cashier@123' });
    await call('post', '/messaging/templates', { id: 0, name: 'Bill SMS', channel: 'Sms', body: 'Thank you for shopping at Sree Traders. Bill {BillNo} for Rs {Amount}.', isActive: true }).catch(() => undefined);
    await call('post', '/messaging/templates', { id: 0, name: 'Onam greetings', channel: 'Sms', body: 'Happy Onam from Sree Traders! 10% off on beverages till 15 Oct.', isActive: true }).catch(() => undefined);
  });

  test('capture screens', async () => {
    await shot('login', async () => {
      await page.goto('/login');
      await page.locator('input[name="u"]').fill('admin');
      await page.locator('input[name="p"]').fill(PASSWORD);
      await page.locator('input[name="counter"]').fill('COUNTER-1');
    });
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page).toHaveURL(/dashboard/);

    // Home
    await shot('dashboard', go('/dashboard'));
    await shot('search-palette', async () => { await page.goto('/dashboard'); await page.keyboard.press('Control+k'); await page.keyboard.type('stock'); });
    await page.keyboard.press('Escape');
    await shot('user-menu', async () => { await page.goto('/dashboard'); await page.locator('.user-btn').click(); });

    // Sales
    await shot('pos', async () => {
      await page.goto('/pos');
      await page.locator('.tile').first().waitFor();
      for (const i of [0, 2, 2, 4, 6]) await page.locator('.tile').nth(i).click();
      await page.locator('select[aria-label="Salesperson"]').selectOption({ label: 'Ravi' });
    });
    await shot('pos-cash-desk', async () => { await page.getByRole('button', { name: /^Cash/ }).click(); await page.locator('.modal').getByRole('button', { name: '+500' }).click(); });
    await page.keyboard.press('Escape');
    await shot('pos-mixed-payment', async () => { await page.getByRole('button', { name: 'Mixed' }).click(); });
    await page.keyboard.press('Escape');
    await shot('sales-invoice-new', async () => {
      await page.goto('/doc/Sales/new');
      await pick('Name, mobile or code', 'Anil');
      await pick('Scan barcode or type item', 'Rice');
      await pick('Scan barcode or type item', 'Coconut');
      await page.locator('label.field', { hasText: 'Salesperson' }).locator('select').selectOption({ label: 'Ravi' });
    });
    await shot('sales-invoice-more', async () => { await page.getByRole('button', { name: 'More…' }).click(); });
    await shot('sales-invoice-saved-b2b', go(`/doc/Sales/${ids.b2b}`));
    await shot('sales-invoice-igst', go(`/doc/Sales/${ids.igst}`));
    await shot('sales-invoice-mixed', async () => {
      await page.goto('/doc/Sales/new');
      await pick('Scan barcode or type item', 'Shampoo');
      await page.locator('label.field', { hasText: 'Payment' }).locator('select').selectOption('Mixed');
      await page.keyboard.press('F6');
    });
    await page.keyboard.press('Escape');
    await shot('sales-return', go('/doc/SalesReturn/new'));
    await shot('estimate', go(`/doc/Estimate/${ids.estimate}`));
    await shot('sales-order', go(`/doc/SalesOrder/${ids.order}`));
    await shot('import-order', async () => { await page.goto('/doc/Sales/new'); await page.getByRole('button', { name: 'Import order…' }).click(); });
    await page.keyboard.press('Escape');
    await shot('delivery-note', go('/doc/DeliveryNote/new'));
    await shot('search-bills', async () => { await page.goto('/documents/Sales'); await page.locator('input[type="date"]').first().fill('2026-04-01'); await page.locator('input[type="date"]').first().dispatchEvent('change'); });

    // Purchase
    await shot('purchase-saved', go(`/doc/Purchase/${ids.purchase1}`));
    await shot('purchase-new', async () => { await page.goto('/doc/Purchase/new'); await pick('Name, mobile or code', 'Sunrise'); await pick('Scan barcode or type item', 'Tea'); });
    await shot('purchase-return', go('/doc/PurchaseReturn/new'));
    await shot('purchase-order', go('/doc/PurchaseOrder/new'));
    await shot('receipt-note', go('/doc/ReceiptNote/new'));
    await shot('search-purchases', async () => { await page.goto('/documents/Purchase'); await page.locator('input[type="date"]').first().fill('2026-04-01'); await page.locator('input[type="date"]').first().dispatchEvent('change'); });

    // Inventory
    await shot('products', go('/masters/products'));
    await shot('product-general', async () => { await page.goto('/masters/products'); await page.getByText('Rose Matta Rice 5kg').click(); await expect(page.locator('h1')).toContainText('Rose Matta'); });
    await shot('product-tax-rates', async () => { await page.getByRole('button', { name: 'Tax & Rates' }).click(); });
    await shot('product-batches', async () => { await page.getByRole('button', { name: 'Batches' }).click(); });
    await shot('product-image', async () => { await page.getByRole('button', { name: 'Image' }).click(); });
    await shot('product-new-category-popup', async () => { await page.goto('/masters/products/new'); await page.getByRole('button', { name: '+ new' }).first().click(); await page.locator('.modal input').first().fill('Snacks'); });
    await page.keyboard.press('Escape');
    await shot('opening-stock', go(`/doc/OpeningStock/${ids.opening}`));
    await shot('stock-adjustment', async () => { await page.goto('/doc/StockAdjustment/new'); await pick('Scan barcode or type item', 'Bread'); });
    await shot('production', async () => {
      await page.goto('/doc/Production/new');
      await page.locator('label.field', { hasText: 'BOM' }).locator('select').selectOption({ index: 1 });
      await page.getByRole('button', { name: 'Load' }).click();
    });
    await shot('bom', async () => { await page.goto('/masters/boms'); await page.getByRole('button', { name: /Plum Cake/ }).click(); });
    await shot('barcode-labels', go(`/inventory/labels?productIds=${[1, 2, 3, 5].join(',')}`));
    await shot('label-design', async () => { await page.getByRole('button', { name: 'Label design…' }).click(); });
    await page.keyboard.press('Escape');
    await shot('stock-summary', go('/reports/stock-summary'));
    await shot('stock-history', go('/reports/stock-history?productId=1'));

    // Accounts
    await shot('receipt-saved', go(`/voucher/Receipt/${ids.receipt}`));
    await shot('receipt-allocate', async () => {
      await page.goto('/voucher/Receipt/new');
      await page.locator('table.line-grid app-lookup input').first().fill('Anil');
      await page.locator('.lookup-panel .opt').first().click();
      await page.locator('table.line-grid input[type="number"]').first().fill('2000');
      await page.getByRole('button', { name: 'Allocate' }).click();
      await page.getByRole('button', { name: 'Auto (oldest first)' }).click();
    });
    await page.keyboard.press('Escape');
    await shot('payment', go('/voucher/Payment/new'));
    await shot('journal', go(`/voucher/Journal/${ids.journal}`));
    await shot('contra', go('/voucher/Contra/new'));
    await shot('debit-note', go('/voucher/DebitNote/new'));
    await shot('credit-note', go('/voucher/CreditNote/new'));
    await shot('voucher-search', async () => { await page.goto('/vouchers'); await page.locator('input[type="date"]').first().fill('2026-04-01'); await page.locator('input[type="date"]').first().dispatchEvent('change'); });
    await shot('opening-balances', go('/accounts/opening'));
    await shot('cheque-register', async () => { await page.goto('/accounts/cheques'); await page.locator('input[type="date"]').first().fill('2026-04-01'); await page.getByRole('button', { name: 'Show' }).click(); });
    await shot('day-book', go('/reports/day-book'));
    await shot('ledger-statement', go(`/reports/ledger-statement?ledgerId=${ids.anil}&ledgerName=Anil%20Kumar`));
    await shot('trial-balance', go('/reports/trial-balance'));
    await shot('profit-loss', go('/reports/profit-loss'));
    await shot('balance-sheet', go('/reports/balance-sheet'));

    // Masters
    await shot('customers', go('/masters/parties/Customer'));
    await shot('customer-editor', go(`/masters/ledger/${ids.meera}`));
    await shot('suppliers', go('/masters/parties/Supplier'));
    await shot('all-ledgers', go('/masters/parties/None'));
    await shot('account-groups', go('/masters/account-groups'));
    await shot('employees', go('/masters/employees'));
    await shot('categories', go('/masters/lookups/category'));
    await shot('manufacturers', go('/masters/lookups/manufacturer'));
    await shot('areas', go('/masters/lookups/area'));
    await shot('price-lists', go('/masters/price-lists'));
    await shot('loyalty', go('/masters/loyalty'));
    await shot('offers', go('/masters/offers'));

    // Reports
    await shot('reports-catalog', go('/reports'));
    await shot('report-sales-daybook', go('/reports/sales-daybook'));
    await shot('report-gstr1-b2b', go('/reports/gstr1-b2b'));
    await shot('report-ageing', go('/reports/ageing'));
    await shot('report-end-of-day', go('/reports/end-of-day'));

    // Settings and tools
    await shot('appearance', go('/settings/appearance'));
    await shot('company-profile', go('/settings/company'));
    await shot('features', go('/settings/features'));
    await shot('screen-options', go('/settings/screens'));
    await shot('numbering', go('/settings/numbering'));
    await shot('print-schemes', go('/settings/printing'));
    await shot('messaging', go('/settings/messaging'));
    await shot('users', go('/settings/users'));
    await shot('roles', go('/settings/roles'));
    await shot('user-log', go('/settings/audit'));
    await shot('companies-backup', go('/settings/companies'));
    await shot('year-end', go('/settings/year-end'));
    await shot('import-export', go('/tools/import-export'));
    await shot('copy-to-company', go('/tools/transfer'));
    await shot('reset-data', go('/tools/reset'));
    await shot('menu-collapsed-rail', async () => { await page.goto('/dashboard'); await page.getByRole('button', { name: 'Toggle menu' }).click(); });
    await page.getByRole('button', { name: 'Toggle menu' }).click();
  });
});
