import { APIRequestContext, Browser, Page, expect, request, test } from '@playwright/test';
import { E2E_LICENSE } from './license';

const API = 'http://localhost:5080/api';
const ADMIN_PASSWORD = 'Admin@12345';

const field = (page: Page, label: string, tag = 'input') =>
  page.locator('label.field', { has: page.locator(`span:text-is("${label}")`) }).locator(tag).first();

/** Signs in through the UI; creates the company first when this spec runs on a fresh database. */
async function signIn(page: Page, userName: string, password: string): Promise<void> {
  await page.goto('/login');
  // /login redirects to /setup on a fresh database once the app has checked; wait until one of them has rendered.
  await expect(page.getByRole('heading', { name: /first-time setup/ }).or(page.getByRole('button', { name: 'Sign in' }))).toBeVisible();
  if (page.url().includes('/setup')) {
    await field(page, 'Company name').fill('E2E Stores');
    await field(page, 'State', 'select').selectOption('32');
    await field(page, 'Admin password (8+ chars, letters and digits)').fill(ADMIN_PASSWORD);
    await field(page, 'Confirm password').fill(ADMIN_PASSWORD);
    await field(page, 'License key', 'textarea').fill(E2E_LICENSE);
    await page.getByRole('button', { name: 'Create company' }).click();
    await expect(page).toHaveURL(/settings\/features/);
    return;
  }
  await field(page, 'User name').fill(userName);
  await field(page, 'Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).not.toHaveURL(/\/login/);
}

async function adminApi(): Promise<APIRequestContext> {
  const anon = await request.newContext();
  const login = await anon.post(`${API}/auth/login`, { data: { userName: 'admin', password: ADMIN_PASSWORD } });
  expect(login.ok()).toBeTruthy();
  const token = (await login.json()).accessToken as string;
  await anon.dispose();
  return request.newContext({ extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
}

async function ask(page: Page, question: string) {
  await expect(page.locator('.msg.bot .chip').first()).toBeVisible(); // greeting with suggestions has loaded
  const before = await page.locator('.msg.bot').count();
  await page.locator('.composer textarea').fill(question);
  await page.locator('.composer textarea').press('Enter');
  await expect(page.locator('.msg.bot')).toHaveCount(before + 1, { timeout: 20_000 });
  return page.locator('.msg.bot .bubble').last();
}

test.describe.serial('FAQ: chatbot by role, Email Us and Manage FAQ', () => {
  let admin: Page;
  let cashier: Page;

  test.beforeAll(async ({ browser }: { browser: Browser }) => {
    admin = await browser.newPage();
    await signIn(admin, 'admin', ADMIN_PASSWORD);
    // A cashier role that can bill but has no settings, year-end or FAQ-management rights.
    const api = await adminApi();
    const role = await api.post(`${API}/roles`, { data: { name: 'FAQ Cashier', isActive: true,
      permissions: [{ screen: 'POS', rights: 3 }, { screen: 'Sales', rights: 3 }, { screen: 'Customer', rights: 3 }] } });
    expect(role.ok()).toBeTruthy();
    const user = await api.post(`${API}/users`, { data: { userName: 'faqcashier', displayName: 'Anu', isActive: true,
      roleId: (await role.json()).id, password: 'Cashier@123' } });
    expect(user.ok()).toBeTruthy();
    await api.dispose();
    cashier = await (await browser.newContext()).newPage();
    await signIn(cashier, 'faqcashier', 'Cashier@123');
  });

  test.afterAll(async () => { await admin.close(); await cashier.close(); });

  test('the FAQ menu has Chatbot and Email Us for everyone, Manage FAQ for the admin', async () => {
    await admin.goto('/faq/chat');
    await expect(admin.locator('.sidebar')).toContainText('FAQ');
    await expect(admin.locator('.sidebar a.nav-link', { hasText: 'Manage FAQ' })).toHaveCount(1);
    await cashier.goto('/faq/chat');
    await expect(cashier.locator('.sidebar a.nav-link', { hasText: 'Chatbot' })).toHaveCount(1);
    await expect(cashier.locator('.sidebar a.nav-link', { hasText: 'Email Us' })).toHaveCount(1);
    await expect(cashier.locator('.sidebar a.nav-link', { hasText: 'Manage FAQ' })).toHaveCount(0);
  });

  test('admin asks where to create roles and opens the screen from the answer', async () => {
    await admin.goto('/faq/chat');
    await expect(admin.locator('.msg.bot .chip').first()).toBeVisible();
    const answer = await ask(admin, 'Where I can create new user roles');
    await expect(answer).toContainText('Settings › Roles & Rights');
    await answer.getByRole('link', { name: /Open this screen/ }).click();
    await expect(admin).toHaveURL(/\/settings\/roles/);
  });

  test('cashier gets billing answers but not year-end closing', async () => {
    await cashier.goto('/faq/chat');
    await expect(await ask(cashier, 'how do I make a bill in POS')).toContainText('Complete sale');
    const closing = await ask(cashier, 'how do I close the financial year');
    await expect(closing).toHaveClass(/restricted/);
    await expect(closing).toContainText('Year-end closing');
    await expect(closing).not.toContainText('Close FY');
  });

  test('Email Us needs a support address, then queues the message', async () => {
    await cashier.goto('/faq/email');
    await expect(cashier.locator('.warn-box')).toContainText("hasn't been set");
    const api = await adminApi();
    expect((await api.put(`${API}/company/settings`, { data: { SupportEmail: 'help@example.com', SupportPhone: '0484 000000' } })).ok()).toBeTruthy();
    await api.dispose();

    await cashier.reload();
    await expect(cashier.locator('.card').filter({ hasText: 'Contact' })).toContainText('help@example.com');
    await field(cashier, 'Subject').fill('Printer cuts the bill short');
    await field(cashier, 'Message', 'textarea').fill('The thermal printer stops before the footer.');
    await cashier.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(cashier.locator('.toast.ok')).toContainText('Message queued');
    await expect(cashier.locator('table.grid')).toContainText('Printer cuts the bill short');
  });

  test('admin sees the cashier\'s question and adds an answer the cashier then gets', async () => {
    await admin.goto('/faq/manage');
    await admin.getByRole('button', { name: /Unanswered/ }).click();
    await expect(admin.locator('table.grid')).toContainText('how do I close the financial year');

    await admin.getByRole('button', { name: '+ New question' }).click();
    const modal = admin.locator('.modal');
    await modal.locator('label.field', { hasText: 'Question' }).first().locator('input').fill('What time does the shop open?');
    await modal.locator('label.field', { hasText: 'Answer' }).locator('textarea').fill('The shop opens at **9 am**.');
    await modal.locator('label.check', { hasText: 'Sales invoices' }).locator('input').check();
    await modal.getByRole('button', { name: 'Save' }).click();
    await expect(admin.locator('.toast.ok')).toContainText('Saved');

    await cashier.goto('/faq/chat');
    await expect(await ask(cashier, 'shop opening time')).toContainText('9 am');
  });
});
