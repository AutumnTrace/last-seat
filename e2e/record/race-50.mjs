// Fifty customers press Hold on F7 at the same moment, over HTTP, against a running API.
//
//   npm run race          (with the API running: npm run demo, or set LASTSEAT_URL)
//
// Prints how many got the seat, how long the losers queued for the row lock, and how long the
// whole burst took. Resets the demo before and after.
const url = process.env.LASTSEAT_URL ?? 'http://localhost:5080';
const clients = Number(process.env.CLIENTS ?? 50);
const rounds = Number(process.env.ROUNDS ?? 5);
const F7 = 1077;

const post = (path, body, headers = {}) =>
  fetch(`${url}${path}`, { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: JSON.stringify(body) });

const pct = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];

const results = [];
for (let round = 1; round <= rounds; round++) {
  await post('/api/demo/reset', {});
  const sessions = await Promise.all(
    Array.from({ length: clients }, async (_, i) => (await (await post('/api/sessions', { name: `client ${i}` })).json()).id),
  );

  const started = performance.now();
  const responses = await Promise.all(
    sessions.map(async (id, i) => {
      const r = await post(`/api/performances/1/holds`, { seatIds: [F7] }, { 'X-Session-Id': id, 'Idempotency-Key': `race-${round}-${i}` });
      return { status: r.status, body: await r.json() };
    }),
  );
  const elapsed = performance.now() - started;

  const won = responses.filter((r) => r.status === 201);
  const lost = responses.filter((r) => r.status === 409);
  const waits = lost.map((r) => r.body.lockWaitMs).sort((a, b) => a - b);
  const decided = responses.map((r) => Date.parse(r.body.decidedAt)).filter(Number.isFinite).sort((a, b) => a - b);
  results.push({ round, won: won.length, lost: lost.length, other: clients - won.length - lost.length, elapsed, waits, spread: decided.at(-1) - decided[0] });
}
await post('/api/demo/reset', {});

console.log(`${clients} clients x ${rounds} rounds against ${url}`);
console.log('round  holds  conflicts  other  lock wait p50 / max (ms)  decisions spread (ms)  burst (ms)');
for (const r of results) {
  console.log(
    `${String(r.round).padStart(5)}  ${String(r.won).padStart(5)}  ${String(r.lost).padStart(9)}  ${String(r.other).padStart(5)}  ` +
      `${pct(r.waits, 50).toFixed(1).padStart(13)} / ${r.waits.at(-1).toFixed(1).padStart(6)}  ` +
      `${r.spread.toFixed(0).padStart(21)}  ${r.elapsed.toFixed(0).padStart(10)}`,
  );
}
const ok = results.every((r) => r.won === 1 && r.lost === clients - 1);
console.log(ok ? 'Every round: exactly one hold.' : 'FAILED: a round did not end with exactly one hold.');
process.exit(ok ? 0 : 1);
