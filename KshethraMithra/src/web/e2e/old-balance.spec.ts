import { APIRequestContext, Page, expect, request, test } from '@playwright/test';
import { E2E_LICENSE } from './license';

const API = 'http://localhost:5180/api';
const ADMIN_PASSWORD = 'Admin@12345';

const field = (page: Page, label: string, tag = 'input') =>
  page.locator('label.field', { has: page.locator(`span:text-is("${label}")`) }).locator(tag).first();

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  // /login redirects to /setup on a fresh database once the app has checked; wait until one of them has rendered.
  await expect(page.getByRole('heading', { name: /first-time setup/ }).or(page.getByRole('button', { name: 'Sign in' }))).toBeVisible();
  if (page.url().includes('/setup')) {
    await field(page, 'Temple name').fill('E2E Stores');
    await field(page, 'State', 'select').selectOption('32');
    await field(page, 'Admin password (8+ chars, letters and digits)').fill(ADMIN_PASSWORD);
    await field(page, 'Confirm password').fill(ADMIN_PASSWORD);
    await field(page, 'License key', 'textarea').fill(E2E_LICENSE);
    await page.getByRole('button', { name: 'Create temple' }).click();
    await expect(page).toHaveURL(/settings\/features/);
    return;
  }
  await field(page, 'User name').fill('admin');
  await field(page, 'Password').fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).not.toHaveURL(/\/login/);
}

async function adminApi(): Promise<APIRequestContext> {
  const anon = await request.newContext();
  const token = (await (await anon.post(`${API}/auth/login`, { data: { userName: 'admin', password: ADMIN_PASSWORD } })).json()).accessToken as string;
  await anon.dispose();
  return request.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
}

async function pick(page: Page, box: string, text: string): Promise<void> {
  await page.locator(box).fill(text);
  await page.locator('.lookup-panel .opt').first().click();
}

test.describe.serial('amount received above the bill: "Pay balance to credit"', () => {
  let page: Page;
  let api: APIRequestContext;
  let customer = 0;
  const balance = async () => (await (await api.get(`${API}/ledgers/${customer}`)).json()).balance as number;

  test.beforeAll(async ({ browser }) => {
    page = await browser.newPage();
    await signIn(page);
    api = await adminApi();
    await api.put(`${API}/company/settings`, { data: { RequireSalesperson: 'false' } });
    const groups = await (await api.get(`${API}/account-groups`)).json() as { id: number; systemCode?: string }[];
    const debtors = groups.find(g => g.systemCode === 'SundryDebtors')!.id;
    const c = await api.post(`${API}/ledgers`, { data: { name: 'Rajesh Dues', accountGroupId: debtors, partyType: 'Customer', stateCode: '32', isActive: true } });
    expect(c.ok(), await c.text()).toBeTruthy();
    customer = (await c.json()).id;
    const p = await api.post(`${API}/products`, { data: { name: 'Gift Hamper', hsnCode: '9985', itemClass: 'Service', gstRate: 0, saleRate: 500, mainUnit: 'NOS', conversionFactor: 1, isActive: true } });
    expect(p.ok(), await p.text()).toBeTruthy();
    const item = (await p.json()).id;
    // He already owes ₹10,000 from an earlier credit bill.
    const credit = await api.post(`${API}/documents`, { data: { voucherType: 'Sales', date: new Date().toISOString(), paymentMode: 'Credit', partyLedgerId: customer,
      lines: [{ productId: item, qty: 20, rate: 500, taxInclusive: false }] } });
    expect(credit.ok(), await credit.text()).toBeTruthy();
  });

  test.afterAll(async () => { await api.dispose(); await page.close(); });

  test('Sales Invoice, cash: buys ₹500, pays ₹700, ₹200 goes to the old balance', async () => {
    await page.goto('/doc/Sales/new');
    // The screen puts the cursor in the item box once it has loaded; wait for that before typing the customer.
    await expect(page.locator('app-lookup input[placeholder^="Scan barcode or type item"]')).toBeFocused();
    await pick(page, 'app-lookup input >> nth=0', 'Rajesh');
    await pick(page, 'app-lookup input[placeholder^="Scan barcode or type item"]', 'Gift Hamper');
    await page.locator('input[aria-label="Amount received"]').fill('700');
    const box = page.locator('.old-bal');
    await expect(box).toContainText('Pay balance to credit');
    await box.locator('input[type="checkbox"]').check();
    await expect(box).toContainText('10,000.00 → ₹ 9,800.00');
    await page.getByRole('button', { name: /^Save/ }).click();
    const desk = page.locator('.modal');                       // cash desk keeps the ₹700 and the tick
    await expect(desk.locator('input[type="number"]').first()).toHaveValue('700');
    await expect(desk).toContainText('Balance to return ₹ 0.00');
    await desk.getByRole('button', { name: 'Save bill' }).click();
    await expect(page.locator('.toast.ok').filter({ hasText: 'received towards old balance' })).toBeVisible({ timeout: 20_000 });
    expect(await balance()).toBe(9800);
  });

  test('Touch POS, card / UPI: buys ₹500, pays ₹700 by UPI, ₹200 goes to the old balance', async () => {
    await page.goto('/pos');
    await expect(page.locator('app-lookup input[placeholder^="Scan barcode / weighing-scale"]')).toBeFocused();
    await pick(page, 'app-lookup input[placeholder="Customer (walk-in)"]', 'Rajesh');
    await pick(page, 'app-lookup input[placeholder^="Scan barcode / weighing-scale"]', 'Gift Hamper');
    await page.getByRole('button', { name: 'Card / UPI' }).click();
    const modal = page.locator('.modal');
    await field(page, 'Card / UPI amount').fill('700');
    await modal.locator('.old-bal input[type="checkbox"]').check();
    await expect(modal.locator('.old-bal')).toContainText('9,800.00 → ₹ 9,600.00');
    await modal.getByRole('button', { name: 'Complete sale' }).click();
    await expect(page.locator('.toast.ok').filter({ hasText: 'received towards old balance' })).toBeVisible({ timeout: 20_000 });
    expect(await balance()).toBe(9600);
  });
});

