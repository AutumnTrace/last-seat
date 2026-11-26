import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Api, Snapshot } from './api';

export type Connection = 'connecting' | 'live' | 'reconnecting' | 'offline';

// Keeps one page's view of the house in sync with PostgreSQL.
//  - The snapshot endpoint is the only source of seat state.
//  - SignalR only says "performance N changed"; the answer is always a fresh snapshot.
//  - Refreshes never overlap: a hint that arrives mid-fetch queues exactly one more fetch, so a
//    slow old response can never land after a newer one.
//  - After a reconnect the page refetches, because hints sent while it was away are gone.
@Injectable()
export class SeatFeed implements OnDestroy {
  private readonly api = inject(Api);
  private hub?: HubConnection;
  private inFlight: Promise<void> | null = null;
  private queued = false;
  private expiryTimer?: ReturnType<typeof setTimeout>;
  private retryTimer?: ReturnType<typeof setTimeout>;
  private stopped = false;
  private performanceId = 0;
  private sessionId: () => string | null = () => null;

  readonly snapshot = signal<Snapshot | null>(null);
  readonly connection = signal<Connection>('connecting');
  readonly refreshes = signal(0);
  // Server time minus local time, from the last snapshot.
  readonly clockOffsetMs = signal(0);

  async start(performanceId: number, sessionId: () => string | null): Promise<void> {
    this.performanceId = performanceId;
    this.sessionId = sessionId;
    this.hub = new HubConnectionBuilder()
      .withUrl('/hubs/seats')
      .withAutomaticReconnect([0, 500, 1000, 2000, 4000, 8000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub.on('seatsChanged', (id: number) => {
      if (id === this.performanceId) void this.refresh();
    });
    this.hub.onreconnecting(() => this.connection.set('reconnecting'));
    this.hub.onreconnected(async () => {
      await this.hub?.invoke('Watch', this.performanceId);
      this.connection.set('live');
      await this.refresh();
    });
    this.hub.onclose(() => {
      if (this.stopped) return;
      this.connection.set('offline');
      this.retryTimer = setTimeout(() => void this.connect(), 3000);
    });
    await this.refresh();
    await this.connect();
  }

  serverNow(): number {
    return Date.now() + this.clockOffsetMs();
  }

  // Resolves once a snapshot fetched after this call has been applied (or failed).
  refresh(): Promise<void> {
    if (this.inFlight) {
      this.queued = true;
      return this.inFlight;
    }
    this.inFlight = this.fetchUntilCurrent().finally(() => (this.inFlight = null));
    return this.inFlight;
  }

  private async fetchUntilCurrent(): Promise<void> {
    do {
      this.queued = false;
      const sentAt = Date.now();
      try {
        const snapshot = await this.api.snapshot(this.performanceId, this.sessionId());
        const receivedAt = Date.now();
        this.clockOffsetMs.set(Date.parse(snapshot.serverNow) - (sentAt + receivedAt) / 2);
        this.snapshot.set(snapshot);
        this.refreshes.update((n) => n + 1);
        this.scheduleExpiryRefresh(snapshot);
      } catch {
        // Leave the last snapshot on screen; the next hint, reconnect or timer will retry.
      }
    } while (this.queued);
  }

  ngOnDestroy(): void {
    this.stopped = true;
    clearTimeout(this.expiryTimer);
    clearTimeout(this.retryTimer);
    void this.hub?.stop();
  }

  private async connect(): Promise<void> {
    if (!this.hub || this.stopped || this.hub.state !== HubConnectionState.Disconnected) return;
    this.connection.set('connecting');
    try {
      await this.hub.start();
      await this.hub.invoke('Watch', this.performanceId);
      this.connection.set('live');
      await this.refresh();
    } catch {
      this.connection.set('offline');
      this.retryTimer = setTimeout(() => void this.connect(), 3000);
    }
  }

  // Holds run out without anyone touching the seat. Refresh just after the earliest one does,
  // so the page changes even if the cleanup sweep's hint is late or lost.
  private scheduleExpiryRefresh(snapshot: Snapshot): void {
    clearTimeout(this.expiryTimer);
    const expiries = snapshot.seats.filter((s) => s.heldUntil).map((s) => Date.parse(s.heldUntil!));
    if (expiries.length === 0) return;
    const wait = Math.min(...expiries) - this.serverNow() + 250;
    this.expiryTimer = setTimeout(() => void this.refresh(), Math.max(250, Math.min(wait, 120_000)));
  }
}
