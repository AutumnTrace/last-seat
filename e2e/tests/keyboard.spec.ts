import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { resetDemo } from './helpers';

test.beforeEach(async ({ baseURL }) => resetDemo(baseURL!));

test('a keyboard-only customer can book on the seat map', async ({ page }) => {
  await page.goto('/book/Kim');
  await expect(page.getByTestId('connection')).toHaveText('live');

  // Tab from the top of the page into the map: one seat is in the tab order.
  const focused = () => page.evaluate(() => document.activeElement?.getAttribute('data-label') ?? document.activeElement?.textContent?.trim());
  for (let i = 0; i < 6 && !(await focused())?.match(/^[A-K]\d+$/); i++) {
    await page.keyboard.press('Tab');
  }
  expect(await focused()).toBe('B2'); // the first free seat

  // N jumps to the next free seat: B2, D12, F7.
  await page.keyboard.press('n');
  await page.keyboard.press('n');
  expect(await focused()).toBe('F7');
  await page.keyboard.press('Space');
  await page.keyboard.press('ArrowRight');
  expect(await focused()).toBe('F8');
  await page.keyboard.press('Space');
  await expect(page.locator('[data-label="F8"]')).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByTestId('selection')).toHaveText('F7–8');

  // Arrow keys reach sold seats too, but they cannot be selected.
  await page.keyboard.press('ArrowUp');
  expect(await focused()).toBe('E8');
  await expect(page.locator('[data-label="E8"]')).toHaveAttribute('aria-disabled', 'true');
  await page.keyboard.press('Space');
  await expect(page.getByTestId('selection')).toHaveText('F7–8');

  await page.keyboard.press('Tab');
  await expect(page.getByTestId('hold-button')).toBeFocused();
  await page.keyboard.press('Enter');

  // After the hold, focus moves to Confirm.
  await expect(page.getByTestId('confirm')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('reference')).toHaveText(/^LS-/);
  await expect(page.getByTestId('announcement')).toContainText('Booked F7 and F8');
});

test('the list view books with native checkboxes', async ({ page }) => {
  await page.goto('/book/Kim');
  await page.getByRole('button', { name: 'List' }).click();
  await page.getByRole('checkbox', { name: /^F7/ }).check();
  await page.getByRole('checkbox', { name: /^F8/ }).check();
  await page.getByTestId('hold-button').click();
  await expect(page.getByTestId('hold')).toContainText('F7–8');
  await expect(page.getByRole('checkbox', { name: /^F7/ })).toBeDisabled();
});

for (const path of ['/', '/book/Kim', '/box-office']) {
  test(`no serious accessibility violations on ${path}`, async ({ page }) => {
    await page.goto(path);
    await page.waitForLoadState('networkidle');
    const results = await new AxeBuilder({ page }).analyze();
    const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
    expect(serious.map((v) => `${v.id}: ${v.nodes.length} node(s)`)).toEqual([]);
  });
}

test('no serious accessibility violations in the list view and while holding', async ({ page }) => {
  await page.goto('/book/Kim');
  await page.locator('[data-label="F7"]').click();
  await page.getByTestId('hold-button').click();
  await expect(page.getByTestId('hold')).toBeVisible();
  await page.getByRole('button', { name: 'List' }).click();
  const results = await new AxeBuilder({ page }).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious.map((v) => `${v.id}: ${v.nodes.length} node(s)`)).toEqual([]);
});
