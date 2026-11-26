import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output, signal } from '@angular/core';
import { SeatView } from '../core/api';
import { groupRows, nextFocus } from '../core/seat-nav';

const SEAT_W = 24;
const SEAT_H = 22;
const PAD = 40;

// The seat map. Every seat is a focusable checkbox; one seat at a time is in the tab order
// (roving tabindex) and the arrow keys move between seats. See core/seat-nav.ts for the keys.
@Component({
  selector: 'app-seat-map',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './seat-map.html',
  styleUrl: './seat-map.css',
})
export class SeatMap {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly seats = input.required<SeatView[]>();
  readonly selected = input<ReadonlySet<number>>(new Set());
  readonly pending = input<ReadonlySet<number>>(new Set());
  readonly readonly = input(false);
  readonly toggle = output<number>();

  private readonly focusId = signal<number | null>(null);

  protected readonly rows = computed(() => groupRows(this.seats()));

  protected readonly box = computed(() => {
    const seats = this.seats();
    const xs = seats.map((s) => s.x);
    const ys = seats.map((s) => s.y);
    const minX = Math.min(...xs) - SEAT_W / 2 - PAD;
    const maxX = Math.max(...xs) + SEAT_W / 2 + PAD;
    const minY = Math.min(...ys) - SEAT_H / 2 - 70;
    const maxY = Math.max(...ys) + SEAT_H / 2 + 16;
    return { minX, minY, width: maxX - minX, height: maxY - minY, stageY: minY + 14 };
  });

  // The seat in the tab order: the last focused one, else the first seat you could select.
  protected readonly tabStop = computed(() => {
    const current = this.focusId();
    const seats = this.seats();
    if (current !== null && seats.some((s) => s.id === current)) return current;
    return (seats.find((s) => this.canSelect(s)) ?? seats[0])?.id ?? null;
  });

  protected readonly seatW = SEAT_W;
  protected readonly seatH = SEAT_H;

  canSelect(seat: SeatView): boolean {
    return seat.state === 'available' || (seat.state === 'held' && seat.yours);
  }

  protected status(seat: SeatView): string {
    if (this.pending().has(seat.id)) return 'pending';
    if (seat.state === 'booked') return seat.yours ? 'booked-yours' : 'booked';
    if (seat.state === 'held') return seat.yours ? 'held-yours' : 'held';
    return this.selected().has(seat.id) ? 'selected' : 'available';
  }

  protected ariaLabel(seat: SeatView): string {
    const where = seat.block === 0 ? 'left' : seat.block === 1 ? 'centre' : 'right';
    const state =
      seat.state === 'booked'
        ? seat.yours
          ? 'booked by you'
          : 'sold'
        : seat.state === 'held'
          ? seat.yours
            ? 'held for you'
            : 'held by another customer'
          : 'available';
    return `Row ${seat.row}, seat ${seat.number}, ${where}, ${state}`;
  }

  protected checked(seat: SeatView): boolean {
    return this.selected().has(seat.id) || (seat.state === 'held' && seat.yours);
  }

  protected onKey(event: KeyboardEvent, seat: SeatView): void {
    if (event.key === ' ' || event.key === 'Enter') {
      event.preventDefault();
      this.activate(seat);
      return;
    }
    const next = nextFocus(this.seats(), seat.id, event.key, (s) => this.canSelect(s));
    if (next === null) return;
    event.preventDefault();
    this.focusId.set(next);
    this.host.nativeElement.querySelector<SVGGElement>(`[data-seat="${next}"]`)?.focus();
  }

  protected activate(seat: SeatView): void {
    this.focusId.set(seat.id);
    if (this.readonly() || !this.canSelect(seat) || this.pending().size > 0) return;
    this.toggle.emit(seat.id);
  }
}
