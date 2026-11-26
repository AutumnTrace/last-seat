import { SeatView } from './api';

// Keyboard movement on the seat map. Pure, so it is unit tested without a browser.
//   Left/Right   previous/next seat in the row (aisles are skipped, not stopped at)
//   Up/Down      the seat in the next row nearest horizontally
//   Home/End     first/last seat in the row
//   n / N        next/previous seat you could select, in reading order
export function nextFocus(seats: SeatView[], fromId: number, key: string, canSelect: (s: SeatView) => boolean): number | null {
  const rows = groupRows(seats);
  const from = seats.find((s) => s.id === fromId);
  if (!from) return seats[0]?.id ?? null;
  const rowIndex = rows.findIndex((r) => r[0].row === from.row);
  const row = rows[rowIndex];
  const i = row.findIndex((s) => s.id === fromId);

  switch (key) {
    case 'ArrowLeft':
      return row[Math.max(0, i - 1)].id;
    case 'ArrowRight':
      return row[Math.min(row.length - 1, i + 1)].id;
    case 'Home':
      return row[0].id;
    case 'End':
      return row[row.length - 1].id;
    case 'ArrowUp':
    case 'ArrowDown': {
      const target = rows[rowIndex + (key === 'ArrowUp' ? -1 : 1)];
      if (!target) return fromId;
      return target.reduce((best, s) => (Math.abs(s.x - from.x) < Math.abs(best.x - from.x) ? s : best)).id;
    }
    case 'n':
    case 'N': {
      const order = rows.flat();
      const start = order.findIndex((s) => s.id === fromId);
      const step = key === 'n' ? 1 : -1;
      for (let k = 1; k <= order.length; k++) {
        const s = order[(start + step * k + order.length * k) % order.length];
        if (canSelect(s)) return s.id;
      }
      return fromId;
    }
    default:
      return null;
  }
}

export function groupRows(seats: SeatView[]): SeatView[][] {
  const byRow = new Map<string, SeatView[]>();
  for (const s of [...seats].sort((a, b) => a.id - b.id)) {
    const row = byRow.get(s.row) ?? [];
    row.push(s);
    byRow.set(s.row, row);
  }
  return [...byRow.values()].map((r) => r.sort((a, b) => a.number - b.number));
}

// "F7–8" for a run in one row, otherwise "D12, H4".
export function seatsLabel(seats: Pick<SeatView, 'row' | 'number' | 'label'>[]): string {
  const sorted = [...seats].sort((a, b) => (a.row === b.row ? a.number - b.number : a.row.localeCompare(b.row)));
  const run =
    sorted.length > 1 &&
    sorted.every((s, k) => s.row === sorted[0].row && s.number === sorted[0].number + k);
  return run ? `${sorted[0].label}–${sorted[sorted.length - 1].number}` : sorted.map((s) => s.label).join(', ');
}
