import { Browser, BrowserContext, Page, expect, request } from '@playwright/test';

export const F7 = '[data-label="F7"]';
export const F8 = '[data-label="F8"]';

export async function resetDemo(baseURL: string): Promise<void> {
  const api = await request.newContext({ baseURL });
  expect((await api.post('/api/demo/reset')).status()).toBe(204);
  await api.dispose();
}

export async function demo(baseURL: string, path: string, data: object): Promise<void> {
  const api = await request.newContext({ baseURL });
  expect((await api.post(`/api/demo/${path}`, { data })).ok()).toBeTruthy();
  await api.dispose();
}

// A customer is a browser context: its own cookies and its own sessionStorage.
export async function customer(browser: Browser, baseURL: string, name: string): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ baseURL, viewport: { width: 1280, height: 860 } });
  const page = await context.newPage();
  await page.goto(`/book/${name}`);
  await expect(page.getByTestId('connection')).toHaveText('live');
  await expect(page.locator(F7)).toHaveAttribute('data-state', 'available');
  return { context, page };
}

export function seat(page: Page, label: string) {
  return page.locator(`[data-label="${label}"]`);
}
