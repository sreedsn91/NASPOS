import { Locator, Page, expect, test } from '@playwright/test';
import { E2E_LICENSE } from './license';

const ADMIN_PASSWORD = 'Admin@12345';
const DEVOTEE = 'Raman Nair';

/** The input of a labelled field, within the page or a part of it (e.g. a modal). */
const field = (root: Page | Locator, label: string, tag = 'input') => {
  const page = 'goto' in root ? root : root.page();
  return root.locator('label.field', { has: page.locator(`span:text-is("${label}")`) }).locator(tag).first();
};

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
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

const tile = (page: Page, name: string) => page.locator('button.tile', { has: page.locator('.t-name', { hasText: new RegExp(`^${name}$`) }) });

test('temple counter: book today and every Friday, pooja list, calendar and donation', async ({ page }) => {
  await signIn(page);

  // --- Counter: Pushpanjali today + Archana on 4 Fridays ---
  await page.goto('/vazhipadu');
  await expect(tile(page, 'Archana')).toBeVisible();
  await field(page, 'Name on receipt').fill(DEVOTEE);
  await field(page, 'Phone').fill('9847000001');

  await tile(page, 'Pushpanjali').click();
  const lines = page.locator('.line');
  await expect(lines).toHaveCount(1);
  await lines.nth(0).getByPlaceholder('Devotee name').fill(DEVOTEE);
  await lines.nth(0).getByLabel('Star').selectOption({ label: 'Thiruvonam · തിരുവോണം' });

  await tile(page, 'Archana').click();
  await expect(lines).toHaveCount(2);
  // A new line copies the previous devotee and star.
  await expect(lines.nth(1).getByPlaceholder('Devotee name')).toHaveValue(DEVOTEE);
  await lines.nth(1).getByRole('button', { name: 'Change days' }).click();
  const modal = page.locator('.modal');
  await field(modal, 'When', 'select').selectOption({ label: 'Every week on a day (e.g. every Friday)' });
  await field(modal, 'Day', 'select').selectOption({ label: 'Friday · വെള്ളി' });
  await field(modal, 'or number of times').fill('4');
  await expect(modal.locator('.info-box')).toContainText('4 day(s)');
  await expect(modal.locator('tbody tr')).toHaveCount(4);
  await modal.getByRole('button', { name: 'Use these days' }).click();
  await expect(lines.nth(1).locator('.when .badge')).toContainText('Every Friday');

  // 20 (Pushpanjali today) + 4 × 30 (Archana) = 140
  await expect(page.locator('.total')).toContainText('140.00');
  await page.getByRole('button', { name: /Book & print/ }).click();
  await expect(page.locator('.toast').filter({ hasText: 'Booked' })).toBeVisible();
  await expect(lines).toHaveCount(0);

  // --- Bookings list ---
  await page.goto('/temple/bookings');
  const booking = page.locator('tr.clickable', { hasText: DEVOTEE });
  await expect(booking).toHaveCount(1);
  await expect(booking).toContainText('140.00');
  await booking.click();
  await expect(page.locator('.modal')).toContainText('Every Friday');
  // Reprint opens the print confirmation, preset from the settings: cut print on, no duplicates.
  await page.locator('.modal').getByRole('button', { name: 'Print…' }).click();
  const printBox = page.locator('.modal').last();
  await expect(printBox).toContainText('Print receipt');
  await expect(printBox.getByLabel(/Cut print/)).toBeChecked();
  await expect(printBox.getByLabel('Duplicate receipt')).not.toBeChecked();
  await printBox.getByLabel('Duplicate receipt').check();
  await expect(field(printBox, 'Number of duplicates')).toHaveValue('1');
  await printBox.getByRole('button', { name: "Don't print" }).click();
  await expect(page.locator('.modal')).toHaveCount(1);
  await page.locator('.modal').getByRole('button', { name: 'Close' }).click();

  // --- Vazhipadu print settings: own page, reached from the counter (and Temple menu) ---
  await page.goto('/vazhipadu');
  await page.getByRole('link', { name: 'Print settings', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Vazhipadu print settings' })).toBeVisible();
  await expect(page.getByLabel(/Cut print/)).toBeChecked();
  await expect(page.getByLabel('Ask before printing', { exact: false })).not.toBeChecked();
  await expect(page.locator('.card h2')).toHaveText(['Vazhipadu printing']);
  // --- Pooja list for today: mark the Pushpanjali done ---
  await page.goto('/temple/pooja-list');
  const row = page.locator('tbody tr', { hasText: 'Pushpanjali' }).filter({ hasText: DEVOTEE });
  await expect(row).toHaveCount(1);
  await expect(row).toContainText('Thiruvonam');
  await row.locator('input[type=checkbox]').check();
  await page.getByRole('button', { name: 'Mark done' }).click();
  await expect(row.locator('.badge')).toHaveText('Done');
  await expect(page.locator('.card', { hasText: 'To prepare' })).toContainText('Pushpanjali');

  // --- Calendar ---
  await page.goto('/temple/calendar');
  await expect(page.locator('.cell.today')).toBeVisible();
  await page.locator('.cell.today').click();
  await expect(page.locator('.card', { hasText: 'Star:' })).toBeVisible();
  await expect(page.locator('.card', { hasText: 'Tithi:' })).toBeVisible();

  // --- Donation ---
  await page.goto('/temple/donations');
  await field(page, 'Donor name').fill('Suresh');
  await field(page, 'For', 'select').selectOption({ label: 'Annadanam' });
  await field(page, 'Amount').fill('1001');
  const popup = page.waitForEvent('popup').catch(() => null);
  await page.getByRole('button', { name: /Save & print/ }).click();
  await expect(page.locator('.toast').filter({ hasText: 'saved' })).toBeVisible();
  await (await popup)?.close();
  await expect(page.locator('tbody tr', { hasText: 'Suresh' })).toContainText('1,001.00');

  // --- Dashboard shows today's Malayalam date and the counter shortcut ---
  await page.goto('/dashboard');
  await expect(page.locator('a.info-box')).toContainText('Star');
  await expect(page.getByRole('link', { name: /Vazhipadu counter/ })).toBeVisible();
});

test('Manglish typing in the Malayalam name of a devotee', async ({ page }) => {
  await signIn(page);
  await page.goto('/temple/devotees');
  await page.getByRole('button', { name: 'New devotee' }).click();
  const modal = page.locator('.modal');
  await field(modal, 'Name').fill('Sreekumar Nair');
  const ml = field(modal, 'Name in Malayalam');

  // The app serves its own Malayalam font, so counters without one (or offline) still show Malayalam.
  expect((await page.request.get('/fonts/NotoSansMalayalam-Regular.ttf')).ok()).toBeTruthy();
  expect(await page.evaluate(async () => (await document.fonts.load('16px "Noto Sans Malayalam"', 'ക')).length > 0)).toBe(true);

  // Converted key by key; Backspace removes one English letter of the word being typed.
  await ml.pressSequentially('sreekumar nair kutti');
  await expect(ml).toHaveValue('ശ്രീകുമാർ നായർ കുട്ടി');
  await ml.press('Backspace');
  await expect(ml).toHaveValue('ശ്രീകുമാർ നായർ കുട്ട്');
  await ml.press('Backspace');
  await ml.press('Backspace');
  await ml.pressSequentially('nhi');
  await expect(ml).toHaveValue('ശ്രീകുമാർ നായർ കുന്ഹി');

  // Ctrl+M switches to English letters and back.
  await ml.fill('');
  await ml.pressSequentially('karthika ');
  await ml.press('Control+m');
  await ml.pressSequentially('B2');
  await expect(ml).toHaveValue('കാർത്തിക B2');

  // Ctrl+M again: back to Malayalam. The value reaches the form and is saved.
  await ml.press('Control+m');
  await ml.fill('');
  await ml.pressSequentially('sreekumar');
  await expect(ml).toHaveValue('ശ്രീകുമാർ');
  await modal.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.toast').filter({ hasText: 'Saved' })).toBeVisible();
  await page.locator('tr.clickable', { hasText: 'Sreekumar Nair' }).click();
  await expect(field(page.locator('.modal'), 'Name in Malayalam')).toHaveValue('ശ്രീകുമാർ');
});
