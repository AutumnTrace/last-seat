import { TestBed } from '@angular/core/testing';
import { Api, Snapshot } from './api';
import { SeatFeed } from './seat-feed';

function snapshot(serverNow: string): Snapshot {
  return {
    performance: { id: 1, title: 't', venue: 'v', startsAt: serverNow, holdSeconds: 90 },
    serverNow,
    seats: [],
    hold: null,
    bookings: [],
  };
}

// A snapshot endpoint whose responses the test releases one at a time.
class SlowApi {
  calls = 0;
  inFlight = 0;
  maxInFlight = 0;
  readonly pending: ((s: Snapshot) => void)[] = [];

  snapshot(): Promise<Snapshot> {
    this.calls++;
    this.inFlight++;
    this.maxInFlight = Math.max(this.maxInFlight, this.inFlight);
    return new Promise((resolve) =>
      this.pending.push((s) => {
        this.inFlight--;
        resolve(s);
      }),
    );
  }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('SeatFeed.refresh', () => {
  let feed: SeatFeed;
  let api: SlowApi;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [SeatFeed, { provide: Api, useClass: SlowApi }] });
    feed = TestBed.inject(SeatFeed);
    api = TestBed.inject(Api) as unknown as SlowApi;
  });

  it('never runs two fetches at once and folds requests made mid-fetch into one more fetch', async () => {
    void feed.refresh();
    const asked = feed.refresh();
    void feed.refresh();
    expect(api.calls).toBe(1);

    let done = false;
    void asked.then(() => (done = true));
    api.pending[0](snapshot('2026-12-01T18:00:00.000Z'));
    await flush();

    // The answer to the first fetch is not enough: it may predate the change that was hinted.
    expect(api.calls).toBe(2);
    expect(done).toBe(false);

    api.pending[1](snapshot('2026-12-01T18:00:01.000Z'));
    await asked;
    expect(feed.snapshot()!.serverNow).toBe('2026-12-01T18:00:01.000Z');
    expect(api.maxInFlight).toBe(1);
    expect(feed.refreshes()).toBe(2);
  });

  it('starts a fresh fetch once the previous one has finished', async () => {
    const first = feed.refresh();
    api.pending[0](snapshot('2026-12-01T18:00:00.000Z'));
    await first;
    void feed.refresh();
    expect(api.calls).toBe(2);
  });
});
