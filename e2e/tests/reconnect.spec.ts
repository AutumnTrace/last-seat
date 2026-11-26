import { WebSocketRoute, expect, test } from '@playwright/test';
import { F7, customer, resetDemo, seat } from './helpers';

test.beforeEach(async ({ baseURL }) => resetDemo(baseURL!));

// Hints sent while a browser is disconnected are lost. On reconnect it must refetch the
// snapshot instead of trusting what it last saw.
test('after a dropped connection the page fetches the authoritative snapshot', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL });
  const page = await context.newPage();
  const sockets: WebSocketRoute[] = [];
  let online = true;
  await page.routeWebSocket(/\/hubs\/seats/, (ws) => {
    sockets.push(ws);
    ws.connectToServer();
  });
  // While "offline", every attempt to reconnect fails at negotiation.
  await page.route('**/hubs/seats/negotiate**', (route) => (online ? route.continue() : route.abort()));
  await page.goto('/book/Ada');
  await expect(page.getByTestId('connection')).toHaveText('live');

  online = false;
  await Promise.all(sockets.map((ws) => ws.close()));
  await expect(page.getByTestId('connection')).toHaveText(/reconnecting|offline/);

  const grace = await customer(browser, baseURL!, 'Grace');
  await grace.page.locator(F7).click();
  await grace.page.getByTestId('hold-button').click();
  await expect(grace.page.getByTestId('hold')).toBeVisible();

  // Ada's page missed the hint.
  await expect(seat(page, 'F7')).toHaveAttribute('data-state', 'available');

  online = true;
  await expect(page.getByTestId('connection')).toHaveText('live', { timeout: 20_000 });
  await expect(seat(page, 'F7')).toHaveAttribute('data-state', 'held');

  await context.close();
  await grace.context.close();
});
