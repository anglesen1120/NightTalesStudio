import { Injectable } from '@angular/core';

/** The Web Locks name every tab serialises its `/auth/refresh` on (WR10). */
export const REFRESH_LOCK_NAME = 'nts-refresh';

/**
 * Runs one refresh at a time. With `navigator.locks` the exclusive lock spans every tab of the origin, so only one
 * tab presents the rotating refresh cookie at a time (a second parallel refresh reads as token theft, C1). Without
 * Web Locks (jsdom, lock-less engines) an in-process FIFO mutex keeps the same shape: the cross-tab guarantee is
 * degraded, the in-process one is not.
 */
@Injectable({ providedIn: 'root' })
export class RefreshLock {
  private tail: Promise<void> = Promise.resolve();

  run<T>(fn: () => Promise<T>): Promise<T> {
    if (typeof navigator !== 'undefined' && 'locks' in navigator) {
      return navigator.locks.request(REFRESH_LOCK_NAME, { mode: 'exclusive' }, () => fn());
    }
    const result = this.tail.then(() => fn());
    // `tail` never rejects, so a failed refresh does not poison the queue.
    this.tail = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }
}
