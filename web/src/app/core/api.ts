import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

// Mirrors of the API contracts in src/LastSeat.Api/Allocation/Contracts.cs and Queries/*.cs.

export type SeatState = 'available' | 'held' | 'booked';

export interface SeatView {
  id: number;
  label: string;
  row: string;
  number: number;
  block: number;
  x: number;
  y: number;
  state: SeatState;
  yours: boolean;
  heldUntil: string | null;
}

export interface PerformanceInfo {
  id: number;
  title: string;
  venue: string;
  startsAt: string;
  holdSeconds: number;
}

export interface Snapshot {
  performance: PerformanceInfo;
  serverNow: string;
  seats: SeatView[];
  hold: { holdId: string; seatIds: number[]; createdAt: string; expiresAt: string } | null;
  bookings: { reference: string; seatIds: number[]; createdAt: string }[];
}

export interface SeatRef {
  id: number;
  label: string;
}

export interface HoldCreated {
  holdId: string;
  performanceId: number;
  seats: SeatRef[];
  requestedAt: string;
  decidedAt: string;
  lockWaitMs: number;
  expiresAt: string;
  replacedHoldId: string | null;
}

export interface SeatConflict {
  seatId: number;
  label: string;
  state: 'held' | 'booked';
  since: string | null;
  yours: boolean;
}

export interface Alternative {
  kind: 'together' | 'apart' | 'single';
  label: string;
  seatIds: number[];
}

export interface SeatsUnavailable {
  code: 'seats_unavailable';
  message: string;
  requestedAt: string;
  decidedAt: string;
  lockWaitMs: number;
  conflicts: SeatConflict[];
  stillAvailable: SeatRef[];
  alternatives: Alternative[];
}

export interface BookingConfirmed {
  bookingId: string;
  reference: string;
  holdId: string;
  seats: SeatRef[];
  confirmedAt: string;
  alreadyConfirmed: boolean;
}

export interface HoldExpired {
  code: 'hold_expired';
  message: string;
  holdId: string;
  expiredAt: string;
  requestedAt: string;
  decidedAt: string;
  lockWaitMs: number;
  seats: { seatId: number; label: string; state: 'free' | 'held' | 'booked'; since: string | null; yours: boolean }[];
  canHoldAgain: boolean;
}

export interface Problem {
  code: string;
  message: string;
}

export interface BoxOfficeView {
  performance: PerformanceInfo;
  clock: { now: string; offsetMs: number; frozenAt: string | null; cleanupPaused: boolean };
  counts: { available: number; held: number; booked: number };
  holds: {
    holdId: string;
    session: string;
    seats: string[];
    createdAt: string;
    expiresAt: string;
    expired: boolean;
    seatsStillMarked: boolean;
  }[];
  bookings: { reference: string; session: string; seats: string[]; createdAt: string }[];
  events: {
    id: number;
    at: string;
    session: string | null;
    kind: string;
    outcome: string;
    seats: string[];
    lockWaitMs: number | null;
    detail: string | null;
  }[];
}

export type HoldResult =
  | { kind: 'held'; body: HoldCreated; attempts: number }
  | { kind: 'conflict'; body: SeatsUnavailable; attempts: number }
  | { kind: 'problem'; status: number; body: Problem; attempts: number };

export type ConfirmResult =
  | { kind: 'booked'; body: BookingConfirmed; attempts: number }
  | { kind: 'expired'; body: HoldExpired; attempts: number }
  | { kind: 'problem'; status: number; body: Problem; attempts: number };

const SESSION_HEADER = 'X-Session-Id';
const KEY_HEADER = 'Idempotency-Key';

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  createSession(name: string): Promise<{ id: string; name: string }> {
    return firstValueFrom(this.http.post<{ id: string; name: string }>('/api/sessions', { name }));
  }

  snapshot(performanceId: number, sessionId: string | null): Promise<Snapshot> {
    const headers = sessionId ? new HttpHeaders({ [SESSION_HEADER]: sessionId }) : undefined;
    return firstValueFrom(this.http.get<Snapshot>(`/api/performances/${performanceId}/seats`, { headers }));
  }

  async hold(sessionId: string, performanceId: number, seatIds: number[], onRetry?: (attempt: number) => void): Promise<HoldResult> {
    const r = await this.postIdempotent(`/api/performances/${performanceId}/holds`, { seatIds }, sessionId, onRetry);
    if (r.status === 201) return { kind: 'held', body: r.body as HoldCreated, attempts: r.attempts };
    if (r.status === 409) return { kind: 'conflict', body: r.body as SeatsUnavailable, attempts: r.attempts };
    return { kind: 'problem', status: r.status, body: r.body as Problem, attempts: r.attempts };
  }

  async confirm(sessionId: string, holdId: string, onRetry?: (attempt: number) => void): Promise<ConfirmResult> {
    const r = await this.postIdempotent(`/api/holds/${holdId}/confirm`, {}, sessionId, onRetry);
    if (r.status === 200) return { kind: 'booked', body: r.body as BookingConfirmed, attempts: r.attempts };
    if (r.status === 409 && (r.body as Problem).code === 'hold_expired') {
      return { kind: 'expired', body: r.body as HoldExpired, attempts: r.attempts };
    }
    return { kind: 'problem', status: r.status, body: r.body as Problem, attempts: r.attempts };
  }

  release(sessionId: string, holdId: string): Promise<unknown> {
    return firstValueFrom(
      this.http.post(`/api/holds/${holdId}/release`, {}, { headers: new HttpHeaders({ [SESSION_HEADER]: sessionId }) }),
    );
  }

  boxOffice(performanceId: number): Promise<BoxOfficeView> {
    return firstValueFrom(this.http.get<BoxOfficeView>(`/api/box-office/${performanceId}`));
  }

  demo(path: 'clock/advance' | 'cleanup' | 'reset', body: object = {}): Promise<unknown> {
    return firstValueFrom(this.http.post(`/api/demo/${path}`, body));
  }

  // One request key per user action. If the network drops the request or the response, the
  // retry carries the same key, so the server either does the work once or replays its answer.
  private async postIdempotent(
    url: string,
    body: object,
    sessionId: string,
    onRetry?: (attempt: number) => void,
  ): Promise<{ status: number; body: unknown; attempts: number }> {
    const headers = new HttpHeaders({ [SESSION_HEADER]: sessionId, [KEY_HEADER]: crypto.randomUUID() });
    for (let attempt = 1; ; attempt++) {
      try {
        const response = await firstValueFrom(this.http.post(url, body, { headers, observe: 'response' }));
        return { status: response.status, body: response.body, attempts: attempt };
      } catch (e) {
        if (!(e instanceof HttpErrorResponse)) throw e;
        const transient = e.status === 0 || e.status === 502 || e.status === 503 || e.status === 504;
        if (transient && attempt < 4) {
          onRetry?.(attempt + 1);
          await new Promise((resolve) => setTimeout(resolve, 300 * attempt));
          continue;
        }
        return { status: e.status, body: e.error ?? { code: 'network', message: e.message }, attempts: attempt };
      }
    }
  }
}
