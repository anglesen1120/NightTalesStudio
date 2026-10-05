import { DOCUMENT } from '@angular/common';
import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivityTracker } from '@core/session';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { visiblePoll } from './visible-poll';

const INTERVAL = 1_000;

/** Fake document: settable visibility, and an exact count of registered `visibilitychange` listeners. */
class FakeDocument {
  visibilityState: 'visible' | 'hidden' = 'visible';
  private readonly listeners = new Set<() => void>();
  added = 0;
  removed = 0;

  get listenerCount(): number {
    return this.listeners.size;
  }

  addEventListener(type: string, listener: () => void): void {
    expect(type).toBe('visibilitychange');
    this.added += 1;
    this.listeners.add(listener);
  }

  removeEventListener(type: string, listener: () => void): void {
    expect(type).toBe('visibilitychange');
    this.removed += 1;
    this.listeners.delete(listener);
  }

  setVisibility(state: 'visible' | 'hidden'): void {
    this.visibilityState = state;
    for (const listener of [...this.listeners]) listener();
  }
}

class FakeActivityTracker {
  active = true;
  isActive(): boolean {
    return this.active;
  }
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve!: () => void;
  const promise = new Promise<void>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function setup(run: () => Promise<unknown> | void) {
  const fakeDocument = new FakeDocument();
  const activity = new FakeActivityTracker();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      { provide: DOCUMENT, useValue: fakeDocument },
      { provide: ActivityTracker, useValue: activity },
    ],
  });
  // A child injector stands in for the caller (component/store) whose destruction must clean the poll up.
  const owner = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
  const poll = runInInjectionContext(owner, () => visiblePoll({ intervalMs: INTERVAL, run }));
  return { fakeDocument, activity, owner, poll };
}

describe('visiblePoll', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  it('visiblePoll_NothingRunsBeforeStart', async () => {
    let runs = 0;
    const { fakeDocument } = setup(() => {
      runs += 1;
    });
    await vi.advanceTimersByTimeAsync(INTERVAL * 3);
    expect(runs).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    expect(fakeDocument.listenerCount).toBe(0);
  });

  it('visiblePoll_VisibleAndActive_RunsAtOnceThenEveryInterval', async () => {
    let runs = 0;
    const { poll } = setup(() => {
      runs += 1;
    });
    poll.start();
    expect(runs).toBe(1);
    await vi.advanceTimersByTimeAsync(INTERVAL);
    expect(runs).toBe(2);
    await vi.advanceTimersByTimeAsync(INTERVAL * 2);
    expect(runs).toBe(4);
    poll.stop();
  });

  it('visiblePoll_Hidden_DoesNotRunAndRunsAtOnceOnReturnToVisible', async () => {
    let runs = 0;
    const { poll, fakeDocument } = setup(() => {
      runs += 1;
    });
    poll.start();
    expect(runs).toBe(1);

    fakeDocument.setVisibility('hidden');
    await vi.advanceTimersByTimeAsync(INTERVAL * 5);
    expect(runs).toBe(1);

    fakeDocument.setVisibility('visible');
    expect(runs).toBe(2); // immediate, not at the next tick
    await vi.advanceTimersByTimeAsync(INTERVAL);
    expect(runs).toBe(3); // a full interval after the immediate run
    poll.stop();
  });

  it('visiblePoll_StartWhileHidden_WaitsForVisible', async () => {
    let runs = 0;
    const { poll, fakeDocument } = setup(() => {
      runs += 1;
    });
    fakeDocument.visibilityState = 'hidden';
    poll.start();
    await vi.advanceTimersByTimeAsync(INTERVAL * 2);
    expect(runs).toBe(0);
    fakeDocument.setVisibility('visible');
    expect(runs).toBe(1);
    poll.stop();
  });

  it('visiblePoll_Idle_SkipsTicksAndRunsOnTheNextActiveTick', async () => {
    let runs = 0;
    const { poll, activity } = setup(() => {
      runs += 1;
    });
    poll.start();
    expect(runs).toBe(1);

    activity.active = false;
    await vi.advanceTimersByTimeAsync(INTERVAL * 4);
    expect(runs).toBe(1);

    activity.active = true;
    await vi.advanceTimersByTimeAsync(INTERVAL);
    expect(runs).toBe(2);
    poll.stop();
  });

  it('visiblePoll_ReturnToVisibleWhileIdle_DoesNotRun', () => {
    let runs = 0;
    const { poll, activity, fakeDocument } = setup(() => {
      runs += 1;
    });
    activity.active = false;
    fakeDocument.visibilityState = 'hidden';
    poll.start();
    fakeDocument.setVisibility('visible');
    expect(runs).toBe(0);
    poll.stop();
  });

  it('visiblePoll_SlowRun_NeverOverlapsAndSkipsTicks', async () => {
    const gates: { promise: Promise<void>; resolve: () => void }[] = [];
    let started = 0;
    const { poll, fakeDocument } = setup(() => {
      started += 1;
      const gate = deferred();
      gates.push(gate);
      return gate.promise;
    });
    const gate = (index: number) => {
      const found = gates[index];
      if (found === undefined) throw new Error(`no run #${String(index)}`);
      return found;
    };
    poll.start();
    expect(started).toBe(1);

    await vi.advanceTimersByTimeAsync(INTERVAL * 3);
    expect(started).toBe(1); // ticks skipped while the first run is pending

    fakeDocument.setVisibility('hidden');
    fakeDocument.setVisibility('visible');
    expect(started).toBe(1); // the visibility return does not overlap either

    gate(0).resolve();
    await vi.advanceTimersByTimeAsync(INTERVAL);
    expect(started).toBe(2);
    gate(1).resolve();
    poll.stop();
  });

  it('visiblePoll_RejectedRun_KeepsPolling', async () => {
    let runs = 0;
    const { poll } = setup(() => {
      runs += 1;
      return Promise.reject(new Error('boom'));
    });
    poll.start();
    await vi.advanceTimersByTimeAsync(INTERVAL * 2);
    expect(runs).toBe(3);
    poll.stop();
  });

  it('visiblePoll_Stop_LeavesNoTimerAndRemovesTheListener', async () => {
    let runs = 0;
    const { poll, fakeDocument } = setup(() => {
      runs += 1;
    });
    poll.start();
    expect(vi.getTimerCount()).toBe(1);
    expect(fakeDocument.listenerCount).toBe(1);

    poll.stop();
    expect(vi.getTimerCount()).toBe(0);
    expect(fakeDocument.listenerCount).toBe(0);
    expect(fakeDocument.removed).toBe(fakeDocument.added);

    fakeDocument.setVisibility('hidden');
    fakeDocument.setVisibility('visible');
    await vi.advanceTimersByTimeAsync(INTERVAL * 3);
    expect(runs).toBe(1);

    poll.stop(); // idempotent
    expect(fakeDocument.removed).toBe(1);
  });

  it('visiblePoll_StartTwice_RegistersOneTimerAndOneListener', () => {
    const { poll, fakeDocument } = setup(() => undefined);
    poll.start();
    poll.start();
    expect(vi.getTimerCount()).toBe(1);
    expect(fakeDocument.added).toBe(1);
    poll.stop();
  });

  it('visiblePoll_RestartAfterStop_Works', async () => {
    let runs = 0;
    const { poll, fakeDocument } = setup(() => {
      runs += 1;
    });
    poll.start();
    await vi.advanceTimersByTimeAsync(0); // let the first run settle (a pending run would skip the restart's run)
    poll.stop();
    poll.start();
    expect(runs).toBe(2);
    expect(fakeDocument.listenerCount).toBe(1);
    expect(vi.getTimerCount()).toBe(1);
    poll.stop();
  });

  it('visiblePoll_OwnerDestroyed_ClearsTimerAndListener', async () => {
    let runs = 0;
    const { poll, owner, fakeDocument } = setup(() => {
      runs += 1;
    });
    poll.start();
    expect(vi.getTimerCount()).toBe(1);

    owner.destroy();
    expect(vi.getTimerCount()).toBe(0);
    expect(fakeDocument.listenerCount).toBe(0);
    await vi.advanceTimersByTimeAsync(INTERVAL * 3);
    expect(runs).toBe(1);
  });

  it('visiblePoll_NonPositiveInterval_IsRejected', () => {
    setup(() => undefined);
    const owner = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
    for (const intervalMs of [0, -5, Number.NaN]) {
      expect(() => runInInjectionContext(owner, () => visiblePoll({ intervalMs, run: () => undefined }))).toThrow(
        RangeError,
      );
    }
    owner.destroy();
  });
});
