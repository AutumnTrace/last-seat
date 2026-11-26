import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, BoxOfficeView } from '../core/api';
import { SeatFeed } from '../core/seat-feed';
import { clockTime, countdown, ms } from '../core/time';
import { SeatMap } from '../booking/seat-map';

const PERFORMANCE_ID = 1;

// Staff view: every open hold, every online booking and the decision log, plus the demo
// controls that move the database clock and pause the cleanup sweep.
@Component({
  selector: 'app-box-office',
  imports: [RouterLink, SeatMap],
  providers: [SeatFeed],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './box-office.html',
  styleUrl: './box-office.css',
})
export class BoxOffice implements OnInit {
  private readonly api = inject(Api);
  protected readonly feed = inject(SeatFeed);
  protected readonly view = signal<BoxOfficeView | null>(null);
  protected readonly working = signal(false);
  private readonly tick = signal(Date.now());

  protected readonly clockTime = clockTime;
  protected readonly ms = ms;
  protected readonly countdown = countdown;

  protected readonly serverNow = computed(() => this.tick() + this.feed.clockOffsetMs());

  constructor() {
    const timer = setInterval(() => this.tick.set(Date.now()), 250);
    // Lost races change no seats, so they send no hint. Poll the log as well.
    const poll = setInterval(() => void this.load(), 1500);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(timer);
      clearInterval(poll);
    });
    // Each refreshed seat snapshot (a SignalR hint, a reconnect, an expiry) reloads the tables.
    effect(() => {
      this.feed.refreshes();
      untracked(() => void this.load());
    });
  }

  ngOnInit(): void {
    void this.feed.start(PERFORMANCE_ID, () => null);
  }

  protected left(expiresAt: string): number {
    return Date.parse(expiresAt) - this.serverNow();
  }

  protected async skip(seconds: number): Promise<void> {
    await this.run(() => this.api.demo('clock/advance', { seconds }));
  }

  protected async setCleanup(paused: boolean): Promise<void> {
    await this.run(() => this.api.demo('cleanup', { paused }));
  }

  protected async reset(): Promise<void> {
    await this.run(() => this.api.demo('reset'));
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.working.set(true);
    try {
      await action();
      await this.feed.refresh();
      await this.load();
    } finally {
      this.working.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.view.set(await this.api.boxOffice(PERFORMANCE_ID));
    } catch {
      // Keep the last view; the next refresh retries.
    }
  }
}
