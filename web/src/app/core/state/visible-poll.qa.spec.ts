import { DOCUMENT } from '@angular/common';
import { EnvironmentInjector, createEnvironmentInjector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivityTracker } from '@core/session';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { visiblePoll } from './visible-poll';

// QA (10.2.6-T1): flapping visibility, idle/visible interplay, restart after stop, bad intervals, throwing runs.

class FakeDocument {
  visibilityState: 'visible' | 'hidden' = 'visible';
  private readonly listeners = new Set<() => void>();
  get listenerCount(): number {
    return this.listeners.size;
  }
  addEventListener(_type: string, listener: () => void): void {
    this.listeners.add(listener);
  }
  removeEventListener(_type: string, listener: () => void): void {
    this.listeners.delete(listener);
  }
  setVisibility(state: 'visible' | 'hidden'): void {
    this.visibilityState = state;
    for (const listener of [...this.listeners]) listener();
  }
}

class FakeActivity {
  active = true;
  isActive(): boolean {
    return this.active;
  }
}

function setup(run: () => Promise<unknown> | void, intervalMs = 1_000) {
  const doc = new FakeDocument();
  const activity = new FakeActivity();
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: DOCUMENT, useValue: doc }, { provide: ActivityTracker, useValue: activity }],
  });
  const owner = createEnvironmentInjector([], TestBed.inject(EnvironmentInjector));
  const poll = runInInjectionContext(owner, () => visiblePoll({ intervalMs, run }));
  return { doc, activity, owner, poll };
}

describe('visiblePoll (QA)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  it.each([0, -1, Number.NaN, Number.POSITIVE_INFINITY])('intervalMs %s is rejected', (intervalMs) => {
    expect(() => setup(() => undefined, intervalMs)).toThrow(RangeError);
  });

  it('flapping hidden/visible 50 times never stacks timers or listeners and never overlaps a slow run', async () => {
    let running = 0;
    let maxRunning = 0;
    let runs = 0;
    const { doc, poll } = setup(async () => {
      running += 1;
      runs += 1;
      maxRunning = Math.max(maxRunning, running);
      await new Promise((resolve) => setTimeout(resolve, 5_000));
      running -= 1;
    });
    poll.start();
    for (let i = 0; i < 50; i++) {
      doc.setVisibility('hidden');
      doc.setVisibility('visible');
    }
    expect(vi.getTimerCount()).toBeLessThanOrEqual(2); // interval + the slow run's own timeout
    expect(doc.listenerCount).toBe(1);
    expect(maxRunning).toBe(1);
    await vi.advanceTimersByTimeAsync(6_000);
    poll.stop();
    expect(runs).toBeGreaterThanOrEqual(1);
    expect(maxRunning).toBe(1);
  });

  it('visible but idle never runs; becoming active runs on the next interval tick', async () => {
    let runs = 0;
    const { activity, poll } = setup(() => {
      runs += 1;
    });
    activity.active = false;
    poll.start();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(runs).toBe(0);
    activity.active = true;
    await vi.advanceTimersByTimeAsync(1_000);
    expect(runs).toBe(1);
    poll.stop();
  });

  it('start is idempotent: two starts register one listener and one immediate run', () => {
    let runs = 0;
    const { doc, poll } = setup(() => {
      runs += 1;
    });
    poll.start();
    poll.start();
    expect(runs).toBe(1);
    expect(doc.listenerCount).toBe(1);
    expect(vi.getTimerCount()).toBe(1);
    poll.stop();
  });

  it('stop then start works again (restart) and stop twice is harmless', async () => {
    let runs = 0;
    const { doc, poll } = setup(() => {
      runs += 1;
    });
    poll.start();
    await vi.advanceTimersByTimeAsync(0); // let the first (sync) run release the in-flight flag
    poll.stop();
    poll.stop();
    expect(doc.listenerCount).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    poll.start();
    expect(runs).toBe(2);
    expect(doc.listenerCount).toBe(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(runs).toBe(3);
    poll.stop();
  });

  it('a hidden→visible event after stop() does not run anything', () => {
    let runs = 0;
    const { doc, poll } = setup(() => {
      runs += 1;
    });
    poll.start();
    poll.stop();
    doc.setVisibility('hidden');
    doc.setVisibility('visible');
    expect(runs).toBe(1);
  });

  it('a synchronously throwing run does not kill the loop or wedge the in-flight flag', async () => {
    let runs = 0;
    const { poll } = setup(() => {
      runs += 1;
      throw new Error('boom');
    });
    poll.start();
    await vi.advanceTimersByTimeAsync(3_000);
    expect(runs).toBe(4);
    poll.stop();
  });

  it('a rejecting run does not kill the loop', async () => {
    let runs = 0;
    const { poll } = setup(() => {
      runs += 1;
      return Promise.reject(new Error('boom'));
    });
    poll.start();
    await vi.advanceTimersByTimeAsync(2_000);
    expect(runs).toBe(3);
    poll.stop();
  });

  it('owner destruction while a run is pending leaves no timer or listener, and no further run happens', async () => {
    let runs = 0;
    const { doc, owner, poll } = setup(() => {
      runs += 1;
      return new Promise<void>((resolve) => setTimeout(resolve, 500));
    });
    poll.start();
    owner.destroy();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(runs).toBe(1);
    expect(doc.listenerCount).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('start while hidden registers the listener but runs nothing until visible', () => {
    let runs = 0;
    const { doc, poll } = setup(() => {
      runs += 1;
    });
    doc.visibilityState = 'hidden';
    poll.start();
    expect(runs).toBe(0);
    doc.setVisibility('visible');
    expect(runs).toBe(1);
    poll.stop();
  });
});
