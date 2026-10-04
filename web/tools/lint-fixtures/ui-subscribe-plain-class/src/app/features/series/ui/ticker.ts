import { of } from 'rxjs';

export class Ticker {
  start(): void {
    of(1).subscribe();
  }
}
