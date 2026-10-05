import { DOCUMENT } from '@angular/common';
import { DestroyRef, inject } from '@angular/core';
import { ActivityTracker } from '@core/session';

export interface VisiblePollOptions {
  readonly intervalMs: number;
  /**
   * One poll. It owns its error handling: a rejection is swallowed here only so the loop survives; the next tick
   * runs again. A slow `run` is never overlapped — ticks that arrive while it is pending are skipped.
   */
  readonly run: () => Promise<unknown> | void;
}

export interface VisiblePoll {
  /** Idempotent. Runs once at once when visible and active, then every `intervalMs`. */
  start(): void;
  /** Idempotent. Clears the interval and the `visibilitychange` listener. */
  stop(): void;
}

/**
 * Polls only while the tab is visible **and** `ActivityTracker.isActive()` (README D7/D17): an interval tick that
 * finds the tab hidden or the user idle does nothing, and the first tick (or the return to visible) after that runs
 * at once. Must be created in an injection context: it injects `DOCUMENT`, `ActivityTracker` and `DestroyRef`, and
 * `DestroyRef` teardown calls `stop()`, so the caller's destruction leaves no timer or listener behind. Zoneless:
 * plain timers, no zone API.
 */
export function visiblePoll(options: VisiblePollOptions): VisiblePoll {
  if (!Number.isFinite(options.intervalMs) || options.intervalMs <= 0) {
    throw new RangeError('intervalMs must be a positive finite number');
  }
  const document = inject(DOCUMENT);
  const activity = inject(ActivityTracker);

  let timer: number | undefined;
  let inFlight = false;

  const eligible = (): boolean => document.visibilityState === 'visible' && activity.isActive();

  const tick = (): void => {
    if (inFlight || !eligible()) return;
    inFlight = true;
    void (async () => {
      try {
        await options.run();
      } catch {
        // `run` reports its own failures; the poll keeps going.
      } finally {
        inFlight = false;
      }
    })();
  };

  const startTimer = (): void => {
    window.clearInterval(timer);
    timer = window.setInterval(tick, options.intervalMs);
  };

  const onVisibility = (): void => {
    if (document.visibilityState !== 'visible') return;
    tick();
    startTimer(); // the next tick is a full interval after this immediate run
  };

  function stop(): void {
    if (timer === undefined) return;
    window.clearInterval(timer);
    timer = undefined;
    document.removeEventListener('visibilitychange', onVisibility);
  }

  function start(): void {
    if (timer !== undefined) return;
    document.addEventListener('visibilitychange', onVisibility);
    startTimer();
    tick();
  }

  inject(DestroyRef).onDestroy(stop);

  return { start, stop };
}
