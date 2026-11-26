import { Injectable, inject } from '@angular/core';
import { Api } from './api';

export interface DemoSession {
  id: string;
  name: string;
}

// A demo session per browser tab (sessionStorage), so two tabs of one browser are two customers.
@Injectable({ providedIn: 'root' })
export class Sessions {
  private readonly api = inject(Api);

  async forName(name: string): Promise<DemoSession> {
    const stored = sessionStorage.getItem(this.key(name));
    if (stored) return JSON.parse(stored) as DemoSession;
    return this.renew(name);
  }

  // After a demo reset the server no longer knows the old id.
  async renew(name: string): Promise<DemoSession> {
    const session = await this.api.createSession(name);
    sessionStorage.setItem(this.key(name), JSON.stringify(session));
    return session;
  }

  private key(name: string): string {
    return `last-seat.session.${name.toLowerCase()}`;
  }
}
