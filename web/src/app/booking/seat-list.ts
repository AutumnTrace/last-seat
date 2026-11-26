import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { SeatView } from '../core/api';
import { groupRows } from '../core/seat-nav';

// The same house as a list of native checkboxes, one group per row that still has seats.
@Component({
  selector: 'app-seat-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="summary">{{ summary() }}</p>
    @for (row of openRows(); track row.name) {
      <fieldset>
        <legend>Row {{ row.name }}</legend>
        @for (seat of row.seats; track seat.id) {
          <label [class.disabled]="readonly()">
            <input
              type="checkbox"
              [checked]="selected().has(seat.id) || seat.yours"
              [disabled]="readonly()"
              [attr.data-seat]="seat.id"
              (change)="toggle.emit(seat.id)"
            />
            {{ seat.label }}
            <span class="where">{{ where(seat) }}{{ seat.yours ? ', held for you' : '' }}</span>
          </label>
        }
      </fieldset>
    } @empty {
      <p>No seats are free right now.</p>
    }
    @if (closedRows().length > 0) {
      <p class="closed">No free seats in row{{ closedRows().length > 1 ? 's' : '' }} {{ closedRows().join(', ') }}.</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .summary {
      margin: 0 0 12px;
      color: var(--muted);
    }
    fieldset {
      border: 1px solid var(--line);
      border-radius: 8px;
      margin: 0 0 10px;
      padding: 8px 12px 10px;
    }
    legend {
      font-weight: 600;
      padding: 0 4px;
    }
    label {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      margin: 4px 18px 4px 0;
      min-height: 32px;
    }
    input {
      width: 18px;
      height: 18px;
    }
    .where,
    .closed {
      color: var(--muted);
    }
    .disabled {
      opacity: 0.7;
    }
  `,
})
export class SeatList {
  readonly seats = input.required<SeatView[]>();
  readonly selected = input<ReadonlySet<number>>(new Set());
  readonly readonly = input(false);
  readonly toggle = output<number>();

  private readonly rows = computed(() =>
    groupRows(this.seats()).map((seats) => ({
      name: seats[0].row,
      seats: seats.filter((s) => s.state === 'available' || (s.state === 'held' && s.yours)),
    })),
  );

  protected readonly openRows = computed(() => this.rows().filter((r) => r.seats.length > 0));
  protected readonly closedRows = computed(() => this.rows().filter((r) => r.seats.length === 0).map((r) => r.name));
  protected readonly summary = computed(() => {
    const free = this.seats().filter((s) => s.state === 'available').length;
    return `${free} of ${this.seats().length} seats are free.`;
  });

  protected where(seat: SeatView): string {
    return seat.block === 0 ? 'left' : seat.block === 1 ? 'centre' : 'right';
  }
}
