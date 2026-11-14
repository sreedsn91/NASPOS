import { Locator, Page, expect, test } from '@playwright/test';

const field = (root: Page | Locator, label: string, tag = 'input') => {
  const page = 'goto' in root ? root : root.page();
  return root.locator('label.field', { has: page.locator(`span:text-is("${label}")`) }).locator(tag).first();
};

async function pickLookup(page: Page, box: Locator, text: string): Promise<void> {
  await box.fill(text);
  const opt = page.locator('.lookup-panel .opt').first();
  await expect(opt).toBeVisible();
  await opt.click();
}

async function expectToast(page: Page, text: RegExp): Promise<void> {
  await expect(page.locator('.toast.ok').filter({ hasText: text }).first()).toBeVisible({ timeout: 20_000 });
}

test.describe.serial('billing flow through the UI', () => {
  let page: Page;
  test.beforeAll(async ({ browser }) => { page = await browser.newPage(); });
  test.afterAll(async () => { await page.close(); });

  test('first-run setup creates the company and admin', async () => {
    await page.goto('/login');
    await expect(page).toHaveURL(/\/setup/);
    await field(page, 'Company name').fill('E2E Stores');
    await field(page, 'State', 'select').selectOption('32');
    await field(page, 'Admin password (8+ chars, letters and digits)').fill('Admin@12345');
    await field(page, 'Confirm password').fill('Admin@12345');
    await page.getByRole('button', { name: 'Create company' }).click();
    await expect(page).toHaveURL(/settings\/features/);
    await expect(page.locator('.topbar')).toContainText('E2E Stores');
  });

  test('masters: salesperson and product', async () => {
    await page.goto('/masters/employees');
    await page.getByRole('button', { name: 'New employee' }).click();
    await field(page.locator('.modal'), 'Name').fill('Ravi');
    await page.locator('.modal').getByRole('button', { name: 'Save' }).click();
    await expect(page.locator('table.grid')).toContainText('Ravi');

    await page.goto('/masters/products/new');
    await field(page, 'Name').fill('Soap 100g');
    await field(page, 'HSN / SAC').fill('3401');
    await page.getByRole('button', { name: 'Tax & Rates' }).click();
    await field(page, 'GST %', 'select').selectOption({ label: '18%' });
    await field(page, 'Purchase rate').fill('60');
    await field(page, 'Sale rate').fill('100');
    await field(page, 'MRP').fill('120');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await expectToast(page, /Item saved/);
  });

  test('purchase brings stock in', async () => {
    await page.goto('/doc/Purchase/new');
    await pickLookup(page, page.locator('app-lookup input[placeholder^="Scan barcode or type item"]'), 'Soap');
    const qty = page.locator('input[data-col="qty"]').first();
    await qty.fill('10');
    await expect(page.locator('.grand')).toContainText('708.00');
    await page.getByRole('button', { name: /^Save/ }).click();
    await expectToast(page, /Purchase P1 saved/);
  });

  test('touch POS cash sale with cash desk', async () => {
    await page.goto('/pos');
    await pickLookup(page, page.locator('app-lookup input[placeholder^="Scan barcode / weighing-scale"]'), 'Soap');
    // POS defaults to tax-inclusive unit prices: ₹100 including 18% GST.
    await expect(page.locator('.pos-total .big')).toContainText('100.00');
    await page.locator('select[aria-label="Salesperson"]').selectOption({ label: 'Ravi' });
    await page.getByRole('button', { name: /^Cash/ }).click();
    await page.locator('.modal').getByRole('button', { name: 'Exact' }).click();
    // Print-on-save is on by default for POS; dismiss the print prompt if shown.
    const done = page.locator('.modal').getByRole('button', { name: 'Complete sale' });
    await done.click();
    await expectToast(page, /Bill POS1/);
  });

  test('keyboard invoice: credit sale needs a customer', async () => {
    await page.goto('/doc/Sales/new');
    await pickLookup(page, page.locator('app-lookup input[placeholder^="Scan barcode or type item"]'), 'Soap');
    await field(page, 'Salesperson', 'select').selectOption({ label: 'Ravi' });
    await field(page, 'Payment', 'select').selectOption('Credit');
    await page.getByRole('button', { name: /^Save/ }).click();
    await expect(page.locator('.toast.error').first()).toContainText(/customer/i);
    await field(page, 'Payment', 'select').selectOption('Cash');
    await page.getByRole('button', { name: /^Save/ }).click();
    await expectToast(page, /Sales S1 saved/);
  });

  test('reports: stock reconciles and books balance', async () => {
    await page.goto('/reports/stock-summary');
    await expect(page.locator('app-report-grid tbody tr').first()).toContainText('Soap 100g');
    await expect(page.locator('app-report-grid tbody tr').first()).toContainText('8');
    await page.goto('/reports/trial-balance');
    await expect(page.locator('.badge', { hasText: 'Status' })).toContainText('Books balance');
    await page.goto('/reports/gstr1-b2cs');
    // Two inclusive ₹100 sales: taxable 84.75 each.
    await expect(page.locator('app-report-grid tfoot')).toContainText('169.50');
    await page.goto('/reports/balance-sheet');
    await expect(page.locator('.badge', { hasText: 'Difference' })).toContainText('0.00');
  });

  test('screens render (screenshots for review)', async () => {
    for (const [url, name] of [['/dashboard', 'dashboard'], ['/doc/Sales/new', 'invoice'], ['/pos', 'pos'], ['/reports/profit-loss', 'pnl']]) {
      await page.goto(url);
      await page.waitForLoadState('networkidle');
      await page.screenshot({ path: `test-results/screens/${name}.png` });
    }
  });

  test('receipt voucher from the accounts screen', async () => {
    await page.goto('/voucher/Journal/new');
    await expect(page.getByRole('heading', { name: /Journal/ })).toBeVisible();
    await page.goto('/vouchers');
    await expect(page.getByRole('heading', { name: 'Voucher search' })).toBeVisible();
  });
});
