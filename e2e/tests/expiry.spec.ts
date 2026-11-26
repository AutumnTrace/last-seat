import { expect, test } from '@playwright/test';
import { F7, F8, customer, demo, resetDemo, seat } from './helpers';

test.beforeEach(async ({ baseURL }) => resetDemo(baseURL!));

test('a hold that runs out frees the seats in every window', async ({ browser, baseURL }) => {
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');
  await ada.page.locator(F7).click();
  await ada.page.locator(F8).click();
  await ada.page.getByTestId('hold-button').click();
  await expect(seat(grace.page, 'F7')).toHaveAttribute('data-state', 'held');

  const office = await ada.context.newPage();
  await office.goto('/box-office');
  await expect(office.getByTestId('hold-row')).toHaveCount(1);
  await office.getByTestId('skip-90').click();

  await expect(ada.page.getByTestId('notice')).toContainText('Your hold on F7–8 ran out at');
  await expect(seat(ada.page, 'F7')).toHaveAttribute('data-state', 'available');
  await expect(seat(grace.page, 'F7')).toHaveAttribute('data-state', 'available');
  await expect(office.getByTestId('hold-row')).toHaveCount(0);

  await ada.context.close();
  await grace.context.close();
});

test('with cleanup paused, an expired hold is still free to take', async ({ browser, baseURL }) => {
  await demo(baseURL!, 'cleanup', { paused: true });
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');
  await ada.page.locator(F7).click();
  await ada.page.locator(F8).click();
  await ada.page.getByTestId('hold-button').click();
  await expect(ada.page.getByTestId('hold')).toBeVisible();

  await demo(baseURL!, 'clock/advance', { seconds: 91 });
  const office = await grace.context.newPage();
  await office.goto('/box-office');
  // The sweep has not run, so the seat rows still carry the old hold. They are free anyway.
  await expect(office.getByTestId('hold-row')).toContainText('seats still marked until the sweep runs');
  await expect(office.getByTestId('cleanup-state')).toHaveText('cleanup paused');

  await expect(seat(grace.page, 'F7')).toHaveAttribute('data-state', 'available');
  await grace.page.locator(F7).click();
  await grace.page.locator(F8).click();
  await grace.page.getByTestId('hold-button').click();
  await expect(grace.page.getByTestId('hold')).toContainText('F7–8');

  await ada.context.close();
  await grace.context.close();
});

// Ada's tab stops hearing from the server, so it still shows her hold after it ran out and
// Grace took the seats. Pressing Confirm there must not book Grace's seats.
test('a stale browser cannot confirm a hold that now belongs to someone else', async ({ browser, baseURL }) => {
  const ada = await customer(browser, baseURL!, 'Ada');
  const grace = await customer(browser, baseURL!, 'Grace');
  await ada.page.locator(F7).click();
  await ada.page.locator(F8).click();
  await ada.page.getByTestId('hold-button').click();
  await expect(ada.page.getByTestId('confirm')).toBeEnabled();

  await ada.page.route('**/api/performances/1/seats', (route) => route.abort());
  await demo(baseURL!, 'clock/advance', { seconds: 95 });
  await expect(seat(grace.page, 'F7')).toHaveAttribute('data-state', 'available');
  await grace.page.locator(F7).click();
  await grace.page.locator(F8).click();
  await grace.page.getByTestId('hold-button').click();
  await expect(grace.page.getByTestId('hold')).toBeVisible();

  // Still showing the old hold, with time apparently left.
  await expect(ada.page.getByTestId('hold')).toBeVisible();
  await ada.page.getByTestId('confirm').click();
  const expired = ada.page.getByTestId('expired');
  await expect(expired).toContainText('Your hold ran out first');
  await expect(expired).toContainText('F7 is now held by another customer');
  await expect(expired).toContainText('F8 is now held by another customer');
  await expect(ada.page.getByTestId('booked')).toHaveCount(0);
  await expect(ada.page.getByTestId('hold')).toHaveCount(0);

  await grace.page.getByTestId('confirm').click();
  await expect(grace.page.getByTestId('reference')).toBeVisible();

  await ada.context.close();
  await grace.context.close();
});
