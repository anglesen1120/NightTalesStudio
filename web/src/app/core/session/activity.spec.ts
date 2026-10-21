import { TestBed } from '@angular/core/testing';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { ACTIVITY_THROTTLE_MS, ACTIVITY_WINDOWS, ActivityTracker } from './activity';
import { SessionStore } from './session-store';
import { SessionApi } from './session.api';
import { FakeSessionApi, fakeJwt } from './testing/fake-session.api';

const T0 = Date.parse('2026-10-04T10:00:00Z');
const MINUTE = 60_000;
const INPUT_EVENTS = ['pointerdown', 'keydown', 'wheel', 'touchstart'];

function setup(): { tracker: ActivityTracker; store: SessionStore } {
  TestBed.configureTestingModule({
    providers: [...ZONELESS_TEST_PROVIDERS, { provide: SessionApi, useValue: new FakeSessionApi() }],
  });
  return { tracker: TestBed.inject(ActivityTracker), store: TestBed.inject(SessionStore) };
}

function signInAs(store: SessionStore, role: 'user' | 'admin' | 'owner'): void {
  store.signIn({ accessToken: fakeJwt(role), expiresAt: Date.now() + 15 * MINUTE }, role, ['pwd']);
}

describe('ActivityTracker', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(T0);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('ActivityTracker_Throttle_And_Windows', () => {
    const { tracker, store } = setup();
    tracker.start();
    expect(ACTIVITY_THROTTLE_MS).toBe(100);

    // The page load is the first input; events inside the throttle window are dropped.
    expect(tracker.lastInputAt()).toBe(T0);
    vi.setSystemTime(T0 + 50);
    window.dispatchEvent(new Event('keydown'));
    expect(tracker.lastInputAt()).toBe(T0);

    vi.setSystemTime(T0 + 150);
    window.dispatchEvent(new Event('pointerdown'));
    expect(tracker.lastInputAt()).toBe(T0 + 150);
    vi.setSystemTime(T0 + 200);
    window.dispatchEvent(new Event('wheel'));
    expect(tracker.lastInputAt()).toBe(T0 + 150);
    vi.setSystemTime(T0 + 260);
    window.dispatchEvent(new Event('touchstart'));
    expect(tracker.lastInputAt()).toBe(T0 + 260);

    // admin / owner: 25 minutes
    signInAs(store, 'admin');
    expect(tracker.idleWindowMs()).toBe(ACTIVITY_WINDOWS.admin);
    expect(ACTIVITY_WINDOWS.admin).toBe(25 * MINUTE);
    vi.setSystemTime(T0 + 260 + 25 * MINUTE - 1);
    expect(tracker.isActive()).toBe(true);
    vi.setSystemTime(T0 + 260 + 25 * MINUTE);
    expect(tracker.isActive()).toBe(false);
    signInAs(store, 'owner');
    expect(tracker.idleWindowMs()).toBe(ACTIVITY_WINDOWS.admin);

    // user: 13 days
    signInAs(store, 'user');
    expect(tracker.idleWindowMs()).toBe(ACTIVITY_WINDOWS.user);
    expect(ACTIVITY_WINDOWS.user).toBe(13 * 86_400_000);
    expect(tracker.isActive()).toBe(true);
    vi.setSystemTime(T0 + 260 + 13 * 86_400_000);
    expect(tracker.isActive()).toBe(false);

    // Any input revives it.
    window.dispatchEvent(new Event('keydown'));
    expect(tracker.isActive()).toBe(true);
  });

  it('ActivityTracker_Stop_RemovesListeners', () => {
    const { tracker } = setup();
    const added = vi.spyOn(window, 'addEventListener');
    const removed = vi.spyOn(window, 'removeEventListener');
    const docAdded = vi.spyOn(document, 'addEventListener');
    const docRemoved = vi.spyOn(document, 'removeEventListener');
    const cleared = vi.spyOn(window, 'clearInterval');

    tracker.start();
    tracker.start();
    const inputAdds = added.mock.calls.filter(([name]) => INPUT_EVENTS.includes(name));
    expect(inputAdds.map(([name]) => name)).toEqual(INPUT_EVENTS);
    expect(inputAdds.every(([, , options]) => (options as AddEventListenerOptions).passive === true)).toBe(true);
    expect(docAdded.mock.calls.filter(([name]) => name === 'visibilitychange')).toHaveLength(1);

    tracker.stop();
    const inputRemoves = removed.mock.calls.filter(([name]) => INPUT_EVENTS.includes(name));
    expect(inputRemoves.map(([name]) => name)).toEqual(INPUT_EVENTS);
    // The very same handler instances are removed, so no listener is left behind.
    expect(inputRemoves.map(([, handler]) => handler)).toEqual(inputAdds.map(([, handler]) => handler));
    expect(docRemoved.mock.calls.filter(([name]) => name === 'visibilitychange')).toHaveLength(1);
    expect(cleared).toHaveBeenCalledTimes(1);

    vi.setSystemTime(T0 + 5_000);
    window.dispatchEvent(new Event('keydown'));
    expect(tracker.lastInputAt()).toBe(T0);

    // Idempotent.
    tracker.stop();
    expect(cleared).toHaveBeenCalledTimes(1);
  });

  it('stops when its injector is destroyed (no leak)', () => {
    const { tracker } = setup();
    const removed = vi.spyOn(window, 'removeEventListener');
    tracker.start();
    TestBed.resetTestingModule();
    expect(removed.mock.calls.filter(([name]) => INPUT_EVENTS.includes(name))).toHaveLength(INPUT_EVENTS.length);
  });

  it('tracks tab visibility', () => {
    const { tracker } = setup();
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    tracker.start();
    expect(tracker.isVisible()).toBe(true);

    visibility.mockReturnValue('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    expect(tracker.isVisible()).toBe(false);

    visibility.mockReturnValue('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    expect(tracker.isVisible()).toBe(true);
    tracker.stop();
  });

  it('a stopped tracker still answers isActive from the clock', () => {
    const { tracker, store } = setup();
    signInAs(store, 'admin');
    expect(tracker.isActive()).toBe(true);
    vi.setSystemTime(T0 + 26 * MINUTE);
    expect(tracker.isActive()).toBe(false);
  });

  it('throttle boundary: an input exactly 100 ms after the last one is recorded, 99 ms is dropped', () => {
    const { tracker } = setup();
    tracker.start();
    vi.setSystemTime(T0 + 99);
    window.dispatchEvent(new Event('keydown'));
    expect(tracker.lastInputAt()).toBe(T0);
    vi.setSystemTime(T0 + ACTIVITY_THROTTLE_MS);
    window.dispatchEvent(new Event('keydown'));
    expect(tracker.lastInputAt()).toBe(T0 + ACTIVITY_THROTTLE_MS);
    tracker.stop();
  });

  it('user window boundary: 13 days minus 1 ms is still active, a visibility change alone does not revive an idle tab', () => {
    const { tracker, store } = setup();
    tracker.start();
    signInAs(store, 'user');
    vi.setSystemTime(T0 + 13 * 86_400_000 - 1);
    expect(tracker.isActive()).toBe(true);
    vi.setSystemTime(T0 + 13 * 86_400_000);
    expect(tracker.isActive()).toBe(false);
    document.dispatchEvent(new Event('visibilitychange'));
    expect(tracker.isActive()).toBe(false);
    tracker.stop();
  });
});
