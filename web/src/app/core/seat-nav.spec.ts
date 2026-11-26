import { SeatView } from './api';
import { groupRows, nextFocus, seatsLabel } from './seat-nav';

function seat(row: string, number: number, state: SeatView['state'] = 'available', x = number * 30): SeatView {
  const rowIndex = row.charCodeAt(0) - 65;
  return { id: rowIndex * 100 + number, label: `${row}${number}`, row, number, block: number > 3 ? 1 : 0, x, y: rowIndex * 36, state, yours: false, heldUntil: null };
}

const house = [
  ...[1, 2, 3, 4, 5, 6].map((n) => seat('A', n, n === 4 ? 'available' : 'booked')),
  ...[1, 2, 3, 4, 5].map((n) => seat('B', n, 'booked', n * 30 + 15)),
  ...[1, 2, 3, 4, 5, 6].map((n) => seat('C', n, n === 1 || n === 6 ? 'available' : 'held')),
];
const free = (s: SeatView) => s.state === 'available';
const id = (label: string) => house.find((s) => s.label === label)!.id;

describe('nextFocus', () => {
  it('moves along a row and stops at its ends', () => {
    expect(nextFocus(house, id('A3'), 'ArrowRight', free)).toBe(id('A4'));
    expect(nextFocus(house, id('A1'), 'ArrowLeft', free)).toBe(id('A1'));
    expect(nextFocus(house, id('A6'), 'ArrowRight', free)).toBe(id('A6'));
    expect(nextFocus(house, id('C3'), 'Home', free)).toBe(id('C1'));
    expect(nextFocus(house, id('C3'), 'End', free)).toBe(id('C6'));
  });

  it('moves between rows to the horizontally nearest seat', () => {
    // B seats sit half a seat to the right, so from A2 (x=60) the nearest is B1 (x=45).
    expect(nextFocus(house, id('A2'), 'ArrowDown', free)).toBe(id('B1'));
    expect(nextFocus(house, id('C6'), 'ArrowUp', free)).toBe(id('B5'));
    expect(nextFocus(house, id('A2'), 'ArrowUp', free)).toBe(id('A2'));
  });

  it('jumps to the next and previous selectable seat, wrapping around', () => {
    expect(nextFocus(house, id('A1'), 'n', free)).toBe(id('A4'));
    expect(nextFocus(house, id('A4'), 'n', free)).toBe(id('C1'));
    expect(nextFocus(house, id('C6'), 'n', free)).toBe(id('A4'));
    expect(nextFocus(house, id('A4'), 'N', free)).toBe(id('C6'));
  });

  it('ignores other keys', () => {
    expect(nextFocus(house, id('A1'), 'x', free)).toBeNull();
  });
});

describe('seat labels', () => {
  it('names a run by its ends and anything else seat by seat', () => {
    expect(seatsLabel([seat('F', 8), seat('F', 7)])).toBe('F7–8');
    expect(seatsLabel([seat('D', 12), seat('H', 4)])).toBe('D12, H4');
    expect(seatsLabel([seat('F', 7), seat('F', 9)])).toBe('F7, F9');
    expect(groupRows(house).map((r) => r[0].row)).toEqual(['A', 'B', 'C']);
  });
});
