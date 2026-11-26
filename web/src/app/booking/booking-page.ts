import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, BookingConfirmed, HoldExpired, SeatView, SeatsUnavailable, Snapshot } from '../core/api';
import { SeatFeed } from '../core/seat-feed';
import { seatsLabel } from '../core/seat-nav';
import { DemoSession, Sessions } from '../core/session';
import { clockTime, countdown, listLabels, ms } from '../core/time';
import { SeatList } from './seat-list';
import { SeatMap } from './seat-map';

const PERFORMANCE_ID = 1;
const MAX_SEATS = 8;

type Busy = 'holding' | 'confirming' | 'releasing' | null;

@Component({
  selector: 'app-booking-page',
  imports: [RouterLink, SeatMap, SeatList],
  providers: [SeatFeed],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './booking-page.html',
  styleUrl: './booking-page.css',
})
export class BookingPage {
  private readonly api = inject(Api);
  private readonly sessions = inject(Sessions);
  protected readonly feed = inject(SeatFeed);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  // Route parameter: /book/:name
  readonly name = input.required<string>();

  protected readonly session = signal<DemoSession | null>(null);
  protected readonly selected = signal<ReadonlySet<number>>(new Set());
  protected readonly busy = signal<Busy>(null);
  protected readonly retrying = signal(0);
  protected readonly conflict = signal<SeatsUnavailable | null>(null);
  protected readonly expired = signal<HoldExpired | null>(null);
  protected readonly booking = signal<BookingConfirmed | null>(null);
  protected readonly problem = signal<string | null>(null);
  protected readonly notices = signal<string[]>([]);
  protected readonly view = signal<'map' | 'list'>('map');
  protected readonly announcement = signal('');
  private readonly tick = signal(Date.now());


  protected readonly snapshot = this.feed.snapshot;
  protected readonly seats = computed(() => this.snapshot()?.seats ?? []);
  // The snapshot decides, except that a hold the server has just called expired is gone even if
  // this tab could not fetch a fresh snapshot yet.
  protected readonly hold = computed(() => {
    const hold = this.snapshot()?.hold ?? null;
    return hold && hold.holdId === this.expired()?.holdId ? null : hold;
  });
  protected readonly heldSeats = computed(() => {
    const ids = new Set(this.hold()?.seatIds ?? []);
    return this.seats().filter((s) => ids.has(s.id));
  });
  protected readonly selectedSeats = computed(() => this.seats().filter((s) => this.selected().has(s.id)));
  protected readonly pending = computed(() => (this.busy() === 'holding' ? this.selected() : new Set<number>()));
  protected readonly msLeft = computed(() => {
    const hold = this.hold();
    return hold ? Date.parse(hold.expiresAt) - (this.tick() + this.feed.clockOffsetMs()) : 0;
  });
  protected readonly holdFraction = computed(() => {
    const total = (this.snapshot()?.performance.holdSeconds ?? 90) * 1000;
    return Math.max(0, Math.min(1, this.msLeft() / total));
  });

  protected readonly clockTime = clockTime;
  protected readonly ms = ms;
  protected readonly countdown = countdown;
  protected readonly label = (seats: Pick<SeatView, 'row' | 'number' | 'label'>[]) => seatsLabel(seats);
  protected readonly names = (items: { label: string }[]) => listLabels(items.map((i) => i.label));

  constructor() {
    const timer = setInterval(() => this.tick.set(Date.now()), 250);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));

    effect(() => {
      const name = this.name();
      void untracked(async () => {
        const session = await this.sessions.forName(name);
        this.session.set(session);
        await this.feed.start(PERFORMANCE_ID, () => this.session()?.id ?? null);
      });
    });

    // Every new snapshot is checked against what this tab believed.
    let previous: Snapshot | null = null;
    effect(() => {
      const next = this.snapshot();
      if (!next) return;
      untracked(() => this.reconcile(previous, next));
      previous = next;
    });
  }

  protected toggle(seatId: number): void {
    if (this.hold() || this.busy()) return;
    this.conflict.set(null);
    this.expired.set(null);
    this.booking.set(null);
    const next = new Set(this.selected());
    if (next.has(seatId)) next.delete(seatId);
    else if (next.size < MAX_SEATS) next.add(seatId);
    this.selected.set(next);
  }

  protected selectAlternative(seatIds: number[]): void {
    this.selected.set(new Set(seatIds));
    this.conflict.set(null);
    this.focusAfterRender('hold-button');
  }

  protected async holdSeats(): Promise<void> {
    const seatIds = [...this.selected()];
    if (seatIds.length === 0 || this.busy()) return;
    const labels = this.label(this.selectedSeats());
    this.startAction('holding', `Holding ${labels}…`);

    const result = await this.withSession((id) =>
      this.api.hold(id, PERFORMANCE_ID, seatIds, (attempt) => this.retrying.set(attempt)),
    );
    if (result.kind === 'held') {
      this.selected.set(new Set());
      await this.feed.refresh();
      this.announce(`${labels} held for you until ${clockTime(result.body.expiresAt)}. Confirm within 90 seconds.`);
      this.focusAfterRender('confirm');
    } else if (result.kind === 'conflict') {
      this.conflict.set(result.body);
      // Keep what can still be had; drop only what was lost.
      this.selected.set(new Set(result.body.stillAvailable.map((s) => s.id)));
      await this.feed.refresh();
      this.announce(`${listLabels(result.body.conflicts.map((c) => c.label))} went to another customer. Nothing was held.`);
    } else {
      this.problem.set(result.body.message);
    }
    this.busy.set(null);
    this.retrying.set(0);
  }

  protected async confirm(): Promise<void> {
    const hold = this.hold();
    if (!hold || this.busy()) return;
    this.startAction('confirming', 'Confirming…');
    const result = await this.withSession((id) =>
      this.api.confirm(id, hold.holdId, (attempt) => this.retrying.set(attempt)),
    );
    if (result.kind === 'booked') {
      this.booking.set(result.body);
      this.announce(`Booked ${listLabels(result.body.seats.map((s) => s.label))}. Reference ${result.body.reference}.`);
    } else if (result.kind === 'expired') {
      this.expired.set(result.body);
      this.selected.set(new Set(result.body.seats.filter((s) => s.state === 'free').map((s) => s.seatId)));
      this.announce('Your hold ran out before the confirmation was decided. Nothing was booked.');
    } else {
      this.problem.set(result.body.message);
    }
    await this.feed.refresh();
    this.busy.set(null);
    this.retrying.set(0);
  }

  protected async release(): Promise<void> {
    const hold = this.hold();
    const session = this.session();
    if (!hold || !session || this.busy()) return;
    this.startAction('releasing', 'Releasing…');
    await this.api.release(session.id, hold.holdId).catch(() => undefined);
    await this.feed.refresh();
    this.busy.set(null);
    this.announce('Seats released.');
  }

  protected dismissNotice(index: number): void {
    this.notices.update((n) => n.filter((_, i) => i !== index));
  }

  protected conflictStory(c: SeatsUnavailable): string {
    const since = c.conflicts.map((x) => x.since).filter((x): x is string => !!x).sort()[0];
    const taken = listLabels(c.conflicts.map((x) => x.label));
    const held = c.conflicts.every((x) => x.state === 'held');
    if (!since) return `${taken} could not be held.`;
    if (Date.parse(since) >= Date.parse(c.requestedAt)) {
      return (
        `Your request reached the database at ${clockTime(c.requestedAt)}, while another request for the same seats was being decided. ` +
        `That request held them at ${clockTime(since)}. Yours waited ${ms(c.lockWaitMs)} for the seat lock and was decided at ${clockTime(c.decidedAt)}, ` +
        `when ${taken} ${c.conflicts.length > 1 ? 'were' : 'was'} already held.`
      );
    }
    return held
      ? `${taken} had been held by another customer since ${clockTime(since)}, before your request arrived at ${clockTime(c.requestedAt)}.`
      : `${taken} ${c.conflicts.length > 1 ? 'were' : 'was'} already sold.`;
  }

  private reconcile(previous: Snapshot | null, next: Snapshot): void {
    const byId = new Map(next.seats.map((s) => [s.id, s]));

    // A selected seat that someone else just took leaves the selection, with a note.
    if (!this.busy()) {
      const lost = [...this.selected()].map((id) => byId.get(id)).filter((s) => s && s.state !== 'available') as SeatView[];
      if (lost.length > 0) {
        this.selected.set(new Set([...this.selected()].filter((id) => !lost.some((s) => s.id === id))));
        this.note(`${listLabels(lost.map((s) => s.label))} ${lost.length > 1 ? 'were' : 'was'} just taken by another customer.`);
      }
    }

    // The hold this tab was showing is gone, and not because this tab booked or released it.
    const before = previous?.hold;
    if (before && !next.hold && !this.busy() && !this.booking()) {
      const stillBooked = next.bookings.some((b) => b.seatIds.some((id) => before.seatIds.includes(id)));
      if (!stillBooked) {
        const labels = this.label(before.seatIds.map((id) => byId.get(id)!).filter(Boolean));
        const ranOut = Date.parse(next.serverNow) >= Date.parse(before.expiresAt);
        this.note(ranOut ? `Your hold on ${labels} ran out at ${clockTime(before.expiresAt)}.` : `Your hold on ${labels} ended.`);
      }
    }
  }

  private startAction(busy: Exclude<Busy, null>, announcement: string): void {
    this.busy.set(busy);
    this.conflict.set(null);
    this.expired.set(null);
    this.problem.set(null);
    this.notices.set([]);
    this.announce(announcement);
  }

  // After a demo reset the server has forgotten this tab's session: make a new one, once.
  private async withSession<T extends { kind: string; status?: number }>(call: (sessionId: string) => Promise<T>): Promise<T> {
    const first = await call(this.session()!.id);
    if (first.kind !== 'problem' || first.status !== 401) return first;
    const renewed = await this.sessions.renew(this.name());
    this.session.set(renewed);
    return call(renewed.id);
  }

  // The button to focus may not exist, or may still be disabled, until the next render.
  private focusAfterRender(testId: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`[data-testid="${testId}"]`)?.focus(), {
      injector: this.injector,
    });
  }

  private note(text: string): void {
    this.notices.update((n) => [...n, text]);
    this.announce(text);
  }

  private announce(text: string): void {
    this.announcement.set(text);
  }
}
