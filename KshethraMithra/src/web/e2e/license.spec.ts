import { Page, expect, test } from '@playwright/test';
import { E2E_LICENSE } from './license';

const ADMIN_PASSWORD = 'Admin@12345';
const field = (page: Page, label: string, tag = 'input') =>
  page.locator('label.field', { has: page.locator(`span:text-is("${label}")`) }).locator(tag).first();

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

test('Settings › License shows the company\'s key; a key for another company is refused', async ({ page }) => {
  await signIn(page);
  await page.goto('/settings/license');
  await expect(page.locator('.card').first()).toContainText('Licensed for life.');
  await expect(page.locator('.license-bar')).toHaveCount(0);
  await field(page, 'License key', 'textarea').fill('NAS1-THIS-IS-NOT-A-KEY');
  await page.getByRole('button', { name: 'Apply key' }).click();
  await expect(page.locator('.toast.error')).toContainText(/license key/i);
});
