// Records the README media from the running build: two real browser windows, side by side.
//
//   npm run build && npm run record        (from the repository root)
//
// If nothing answers at LASTSEAT_URL (default http://localhost:5099), the script starts the API
// itself with LASTSEAT_DB as its connection string. Every frame is a screenshot of the two live
// pages at that moment; the caption bar above them is the only thing drawn on top.
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';
import sharp from 'sharp';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const out = join(root, 'docs', 'media');
const url = process.env.LASTSEAT_URL ?? 'http://localhost:5099';
const db = process.env.LASTSEAT_DB ?? 'Host=localhost;Port=5432;Database=lastseat_record;Username=postgres;Password=postgres';

const W = 1000; // each window
const H = 700;
const LABEL = 34; // "Ada's browser" strip above each window
const CAPTION = 58;
const GAP = 16;
const OUTPUT_WIDTH = 1600;

async function up() {
  try {
    return (await fetch(`${url}/health`)).ok;
  } catch {
    return false;
  }
}

async function startApi() {
  if (await up()) return null;
  const port = new URL(url).port;
  const api = spawn('dotnet', ['run', '--project', join(root, 'src', 'LastSeat.Api'), '--no-launch-profile', '--urls', url], {
    env: { ...process.env, ConnectionStrings__LastSeat: db, ASPNETCORE_ENVIRONMENT: 'Production' },
    stdio: 'ignore',
  });
  for (let i = 0; i < 180; i++) {
    if (await up()) return api;
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`API did not come up on port ${port}`);
}

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

async function compose(left, right, caption) {
  const width = W * 2 + GAP * 3;
  const height = CAPTION + LABEL + H + GAP;
  const [a, b] = await Promise.all([left.page.screenshot(), right.page.screenshot()]);
  const overlay = Buffer.from(`
    <svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">
      <style>
        .cap { font: 600 25px 'Segoe UI', system-ui, sans-serif; fill: #ffffff; }
        .lab { font: 600 17px 'Segoe UI', system-ui, sans-serif; fill: #1d2433; }
      </style>
      <rect width="${width}" height="${CAPTION}" fill="#1d2433"/>
      <text x="${GAP + 4}" y="38" class="cap">${esc(caption)}</text>
      <text x="${GAP + 4}" y="${CAPTION + 24}" class="lab">${esc(left.title)}</text>
      <text x="${GAP * 2 + W + 4}" y="${CAPTION + 24}" class="lab">${esc(right.title)}</text>
    </svg>`);
  return sharp({ create: { width, height, channels: 3, background: '#c9ced8' } })
    .composite([
      { input: overlay, left: 0, top: 0 },
      { input: a, left: GAP, top: CAPTION + LABEL },
      { input: b, left: GAP * 2 + W, top: CAPTION + LABEL },
    ])
    .png()
    .toBuffer();
}

async function encode(frames, name) {
  const scaled = await Promise.all(frames.map((f) => sharp(f.png).resize({ width: OUTPUT_WIDTH }).png().toBuffer()));
  const gif = await sharp(scaled, { join: { animated: true } })
    .gif({ delay: frames.map((f) => f.delay), loop: 0, effort: 8, colours: 96, dither: 0 })
    .toBuffer();
  await writeFile(join(out, `${name}.gif`), gif);
  console.log(`${name}.gif: ${frames.length} frames, ${(gif.length / 1024).toFixed(0)} KiB`);
}

async function window(browser, name, path = `/book/${name}`, title = `${name}'s browser`) {
  const context = await browser.newContext({ baseURL: url, viewport: { width: W, height: H } });
  const page = await context.newPage();
  await page.goto(path);
  await page.waitForLoadState('networkidle');
  return { name, title, context, page };
}

async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(150);
}

const reset = () => fetch(`${url}/api/demo/reset`, { method: 'POST' });
const demo = (path, body) =>
  fetch(`${url}/api/demo/${path}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

async function recordRace(browser) {
  await reset();
  const ada = await window(browser, 'Ada');
  const grace = await window(browser, 'Grace');
  const frames = [];
  const frame = async (caption, delay) => frames.push({ png: await compose(ada, grace, caption), delay });

  await frame('F7 and F8 are the last two seats together. Ada and Grace both want them.', 2600);
  await ada.page.locator('[data-label="F7"]').click();
  await ada.page.locator('[data-label="F8"]').click();
  await frame('Ada selects F7 and F8.', 1200);
  await grace.page.locator('[data-label="F7"]').click();
  await grace.page.locator('[data-label="F8"]').click();
  await frame('Grace selects the same two seats.', 1200);

  await Promise.all([ada.page.getByTestId('hold-button').click(), grace.page.getByTestId('hold-button').click()]);
  for (const w of [ada, grace]) {
    await w.page.getByTestId('hold').or(w.page.getByTestId('conflict')).waitFor();
    await settle(w.page);
  }
  const adaWon = await ada.page.getByTestId('hold').isVisible();
  const [winner, loser] = adaWon ? [ada, grace] : [grace, ada];
  await frame(`Both press Hold at once. ${winner.name}'s request locked the seats first.`, 2400);
  await frame(`${loser.name}'s window says what happened, with database times, and keeps nothing half-held.`, 5200);

  await winner.page.getByTestId('confirm').click();
  await winner.page.getByTestId('reference').waitFor();
  await settle(loser.page);
  await frame(`${winner.name} confirms within the 90 seconds. ${loser.name}'s map shows the seats sold.`, 3600);

  await encode(frames, 'race');
  await writeFile(join(out, 'race.png'), await sharp(frames[3].png).resize({ width: OUTPUT_WIDTH }).png().toBuffer());
  await ada.context.close();
  await grace.context.close();
  return { winner: winner.name, loser: loser.name };
}

async function recordStale(browser) {
  await reset();
  const ada = await window(browser, 'Ada');
  const grace = await window(browser, 'Grace');
  const frames = [];
  const frame = async (caption, delay) => frames.push({ png: await compose(ada, grace, caption), delay });

  await ada.page.locator('[data-label="F7"]').click();
  await ada.page.locator('[data-label="F8"]').click();
  await ada.page.getByTestId('hold-button').click();
  await ada.page.getByTestId('hold').waitFor();
  await settle(grace.page);
  await frame('Ada holds F7 and F8 for 90 seconds.', 2400);

  // Ada's tab stops getting answers from the snapshot endpoint, so it keeps showing her hold.
  await ada.page.route('**/api/performances/1/seats', (route) => route.abort());
  await demo('clock/advance', { seconds: 95 });
  await grace.page.locator('[data-label="F7"]').waitFor();
  await settle(grace.page);
  await frame("Ada's tab loses contact. The database clock moves on 95 s: her hold has run out.", 3000);

  await grace.page.locator('[data-label="F7"]').click();
  await grace.page.locator('[data-label="F8"]').click();
  await grace.page.getByTestId('hold-button').click();
  await grace.page.getByTestId('hold').waitFor();
  await frame("Grace holds F7 and F8. Ada's stale tab still shows a Confirm button.", 2800);

  // Contact is back by the time she presses Confirm, so her tab also refreshes afterwards.
  await ada.page.unroute('**/api/performances/1/seats');
  await ada.page.getByTestId('confirm').click();
  await ada.page.getByTestId('expired').waitFor();
  await settle(ada.page);
  await frame('Ada presses Confirm. Checked after locking the seats: expired, and held by Grace.', 5200);

  await encode(frames, 'stale-confirm');
  await ada.context.close();
  await grace.context.close();
}

async function stills(browser) {
  await reset();
  const ada = await window(browser, 'Ada');
  await ada.page.locator('[data-label="F7"]').click();
  await ada.page.locator('[data-label="F8"]').click();
  await ada.page.getByTestId('hold-button').click();
  await ada.page.getByTestId('hold').waitFor();
  await demo('cleanup', { paused: true });
  await demo('clock/advance', { seconds: 30 });

  const office = await window(browser, 'Box office', '/box-office');
  await office.page.setViewportSize({ width: 1280, height: 900 });
  await office.page.getByTestId('hold-row').waitFor();
  await settle(office.page);
  await office.page.screenshot({ path: join(out, 'box-office.png'), fullPage: true });

  await ada.page.getByRole('button', { name: 'List' }).click();
  await settle(ada.page);
  await ada.page.screenshot({ path: join(out, 'list-view.png') });
  await demo('cleanup', { paused: false });
  await ada.context.close();
  await office.context.close();
}

await mkdir(out, { recursive: true });
const api = await startApi();
const browser = await chromium.launch();
try {
  const race = await recordRace(browser);
  console.log(`race: ${race.winner} won, ${race.loser} lost`);
  await recordStale(browser);
  await stills(browser);
  await reset();
} finally {
  await browser.close();
  if (api) {
    if (process.platform === 'win32') spawn('taskkill', ['/pid', String(api.pid), '/t', '/f']);
    else api.kill();
  }
}
