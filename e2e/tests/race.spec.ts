import { expect, test } from '@playwright/test';
import { F7, F8, customer, resetDemo, seat } from './helpers';

test.beforeEach(async ({ baseURL }) => resetDemo(baseURL!));

test('two browsers race for the last two seats together: one holds, the other is told why', async ({ browser, baseURL }) => {
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');

  for (const { page } of [ada, grace]) {
    await page.locator(F7).click();
    await page.locator(F8).click();
    await expect(page.getByTestId('selection')).toHaveText('F7–8');
  }

  await Promise.all([ada.page.getByTestId('hold-button').click(), grace.page.getByTestId('hold-button').click()]);

  // Wait until both windows have an answer, then see who won.
  for (const { page } of [ada, grace]) {
    await expect(page.getByTestId('hold').or(page.getByTestId('conflict'))).toBeVisible();
  }
  const adaWon = await ada.page.getByTestId('hold').isVisible();
  const [winner, loser] = adaWon ? [ada, grace] : [grace, ada];
  await expect(winner.page.getByTestId('hold')).toContainText('F7–8');
  await expect(winner.page.getByTestId('conflict')).toHaveCount(0);
  const conflict = loser.page.getByTestId('conflict');
  await expect(conflict).toContainText('F7 and F8 went to someone else');
  await expect(conflict).toContainText('Nothing was held for you');
  await expect(conflict).toContainText(/waited|had been held/);
  // No other pair is left, so the suggestion is the two nearest single seats.
  await expect(loser.page.getByTestId('alternative')).toHaveCount(1);

  // The loser's map catches up through the SignalR hint and a fresh snapshot.
  await expect(seat(loser.page, 'F7')).toHaveAttribute('data-state', 'held');
  await expect(seat(winner.page, 'F7')).toHaveAttribute('data-state', 'held-yours');

  await winner.page.getByTestId('confirm').click();
  await expect(winner.page.getByTestId('reference')).toHaveText(/^LS-[A-Z2-9]{6}$/);
  await expect(seat(loser.page, 'F8')).toHaveAttribute('data-state', 'booked');

  await ada.context.close();
  await grace.context.close();
});

test('a group that loses one seat keeps the rest selected and can hold them', async ({ browser, baseURL }) => {
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');

  await ada.page.locator(F7).click();
  await ada.page.locator(F8).click();
  await seat(ada.page, 'H4').click();

  // Grace holds H4 first.
  await seat(grace.page, 'H4').click();
  await grace.page.getByTestId('hold-button').click();
  await expect(grace.page.getByTestId('hold')).toBeVisible();

  // Ada's tab drops H4 from the selection as soon as it hears about Grace's hold.
  await expect(ada.page.getByTestId('notice')).toContainText('H4 was just taken by another customer');
  await expect(ada.page.getByTestId('selection')).toHaveText('F7–8');

  await ada.page.getByTestId('hold-button').click();
  await expect(ada.page.getByTestId('hold')).toContainText('F7–8');

  await ada.context.close();
  await grace.context.close();
});

test('a conflict keeps the seats that were still free and offers alternatives that can be selected', async ({ browser, baseURL }) => {
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');

  // Grace holds F8 while Ada's tab cannot hear about it.
  await ada.page.route('**/api/performances/1/seats', (route) => route.abort());
  await ada.page.locator(F7).click();
  await ada.page.locator(F8).click();
  await seat(grace.page, 'F8').click();
  await grace.page.getByTestId('hold-button').click();
  await expect(grace.page.getByTestId('hold')).toBeVisible();

  await ada.page.getByTestId('hold-button').click();
  const conflict = ada.page.getByTestId('conflict');
  await expect(conflict).toContainText('F8 went to someone else');
  await expect(conflict).toContainText('Still free from your selection, and still selected: F7');
  await expect(ada.page.getByTestId('selection')).toHaveText('F7');

  await ada.page.unroute('**/api/performances/1/seats');
  await ada.page.getByTestId('alternative').first().click();
  await ada.page.getByTestId('hold-button').click();
  await expect(ada.page.getByTestId('hold')).toBeVisible();

  await ada.context.close();
  await grace.context.close();
});
