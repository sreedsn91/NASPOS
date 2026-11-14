import { APIRequestContext, Page, expect, request, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Screenshots of the sales-invoice payment flow for the screen guide: payment buttons, cash tendered / balance popup,
 * and the print dialog with the duplicate-copy option. Run on its own:
 *   npx playwright test -c playwright.screens.config.ts payment
 */
const OUT = join(__dirname, '..', '..', '..', 'docs', 'screens');
const BASE = 'http://localhost:5180';
const PASSWORD = 'Demo@12345';

test('sales invoice payment flow screenshots', async ({ browser }) => {
  mkdirSync(OUT, { recursive: true });
  const anon = await request.newContext({ baseURL: BASE, timeout: 300_000 });
  const setup = await (await anon.post('/api/auth/setup', { data: {
    companyName: 'Sree Traders', stateCode: '32', city: 'Kochi', adminUserName: 'admin', adminDisplayName: 'Sreekumar', adminPassword: PASSWORD,
  } })).json();
  await anon.dispose();
  const api: APIRequestContext = await request.newContext({ baseURL: BASE, timeout: 300_000, extraHTTPHeaders: { Authorization: `Bearer ${setup.accessToken}` } });
  const post = async (url: string, data: unknown) => { const r = await api.post('/api' + url, { data }); expect(r.ok(), `${url}: ${await r.text()}`).toBeTruthy(); return r.json(); };

  await post('/employees', { name: 'Ravi', isSalesperson: true, isActive: true, commissionPercent: 1 });
  const groups: any[] = await (await api.get('/api/account-groups')).json();
  await post('/ledgers', { name: 'Anil Kumar', partyType: 'Customer', accountGroupId: groups.find(g => g.systemCode === 'SundryDebtors').id, mobile: '9847012345', stateCode: '32', isActive: true });
  const rice = (await post('/products', { name: 'Rose Matta Rice 5kg', hsnCode: '1006', gstRate: 5, purchaseRate: 260, saleRate: 299, mrp: 320, mainUnit: 'NOS', conversionFactor: 1, isActive: true })).id;
  const oil = (await post('/products', { name: 'Coconut Oil 1L', hsnCode: '1513', gstRate: 5, purchaseRate: 180, saleRate: 215, mrp: 230, mainUnit: 'NOS', conversionFactor: 1, isActive: true })).id;
  await post('/documents', { voucherType: 'OpeningStock', date: new Date().toISOString(), taxType: 'NoTax', acknowledgeWarnings: true,
    lines: [{ productId: rice, qty: 40, rate: 255 }, { productId: oil, qty: 40, rate: 178 }] });
  await api.dispose();

  const page: Page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const shot = async (name: string) => { await page.waitForTimeout(500); await page.screenshot({ path: join(OUT, name), type: 'jpeg', quality: 82 }); console.log('captured', name); };
  const pick = async (placeholderStart: string, text: string) => {
    await page.locator(`app-lookup input[placeholder^="${placeholderStart}"]`).first().fill(text);
    const opt = page.locator('.lookup-panel .opt').first();
    await expect(opt).toBeVisible({ timeout: 60_000 });
    await page.waitForTimeout(800); // let the debounced search finish re-rendering the list
    await opt.click({ timeout: 30_000 });
  };

  await page.goto('/login');
  await page.locator('input[name="u"]').fill('admin');
  await page.locator('input[name="p"]').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/dashboard/, { timeout: 60_000 });

  await page.goto('/doc/Sales/new');
  await pick('Name, mobile or code', 'Anil');
  await pick('Scan barcode or type item', 'Rice');
  await pick('Scan barcode or type item', 'Coconut');
  await page.locator('label.field', { hasText: 'Salesperson' }).locator('select').selectOption({ label: 'Ravi' });
  await expect(page.locator('.pay-modes button.active')).toHaveText(/Cash/);
  await shot('88-invoice-payment-buttons.jpg');

  await page.getByRole('button', { name: /^Save/ }).first().click();
  const desk = page.locator('.modal', { hasText: 'Cash payment' });
  await expect(desk).toBeVisible({ timeout: 30_000 });
  await desk.getByRole('button', { name: '+500' }).click();
  await desk.getByRole('button', { name: '+50' }).click();
  await shot('89-invoice-cash-payment.jpg');

  await desk.getByRole('button', { name: 'Save bill' }).click();
  const printDlg = page.locator('.modal', { hasText: 'Duplicate copy' });
  await expect(printDlg).toBeVisible({ timeout: 60_000 });
  await printDlg.getByLabel(/Duplicate copy/).check();
  await shot('90-print-copies.jpg');
  await printDlg.getByRole('button', { name: 'Skip' }).click();
  await page.close();
});
