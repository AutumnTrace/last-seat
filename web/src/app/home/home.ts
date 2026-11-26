import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-home',
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="home">
      <h1>Last Seat</h1>
      <p class="lead">
        One performance, nearly sold out. Two seats are left together, F7 and F8. Open two booking windows side by side, pick
        the same seats in both and press Hold in each. One gets them; the other is told exactly why it didn't.
      </p>

      <div class="windows">
        <a class="window" href="/book/Ada" target="_blank" rel="noopener">Open Ada's window</a>
        <a class="window" href="/book/Grace" target="_blank" rel="noopener">Open Grace's window</a>
        <a class="window secondary" routerLink="/box-office">Box office</a>
      </div>

      <form class="custom" (submit)="$event.preventDefault()">
        <label for="name">Or book under another name</label>
        <input id="name" maxlength="24" [value]="name()" (input)="name.set($any($event.target).value)" />
        <a [attr.href]="'/book/' + encode(name())" target="_blank" rel="noopener" [class.disabled]="!name().trim()">Open</a>
      </form>

      <h2>What to try</h2>
      <ol>
        <li>Select F7 and F8 in both windows, then press Hold in both within a second or so.</li>
        <li>In the losing window, read the explanation: when each request reached the database, how long it waited for the seat lock, and what it found.</li>
        <li>Open the box office and skip the clock ahead 90 seconds. The winner's hold runs out and the seats come back.</li>
        <li>Pause cleanup first and do it again: the seats are still free to take, because expiry is decided when a seat is locked, not by the sweep.</li>
      </ol>
      <p class="fine">
        Every booking here is simulated: no payment, no account, no ticket. Each browser tab is its own customer, so two tabs of
        one browser work as two people.
      </p>
    </main>
  `,
  styles: `
    .home {
      max-width: 720px;
      margin: 48px auto;
      padding: 0 24px;
    }
    h1 {
      margin: 0 0 8px;
    }
    .lead {
      font-size: 17px;
    }
    .windows {
      display: flex;
      flex-wrap: wrap;
      gap: 12px;
      margin: 24px 0;
    }
    .window {
      padding: 12px 18px;
      border-radius: 10px;
      background: var(--ink);
      color: #fff;
      text-decoration: none;
      font-weight: 600;
    }
    .window.secondary {
      background: var(--panel);
      color: var(--ink);
      border: 1px solid var(--line);
    }
    .custom {
      display: flex;
      align-items: center;
      gap: 10px;
      margin-bottom: 32px;
    }
    .custom input {
      font: inherit;
      padding: 6px 10px;
      border: 1px solid var(--line);
      border-radius: 8px;
    }
    .disabled {
      pointer-events: none;
      opacity: 0.5;
    }
    li {
      margin-bottom: 6px;
    }
    .fine {
      color: var(--muted);
      font-size: 14px;
    }
  `,
})
export class Home {
  protected readonly name = signal('Lin');
  protected readonly encode = encodeURIComponent;
}
