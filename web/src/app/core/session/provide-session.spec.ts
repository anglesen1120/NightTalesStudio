import { DOCUMENT } from '@angular/common';
import { ApplicationInitStatus, PLATFORM_ID } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { ActivityTracker } from './activity';
import { provideSession } from './provide-session';
import { provideSessionFreePaths } from './session-free-path';
import { SessionApi } from './session.api';
import { SessionStore } from './session-store';
import { FakeSessionApi } from './testing/fake-session.api';

class FakeActivityTracker {
  starts = 0;
  start(): void {
    this.starts += 1;
  }
}

function setup(platform: 'browser' | 'server', path = '/') {
  history.pushState(null, '', path);
  // Listed after `provideSession()` so the fake replaces the HTTP repository (later providers win).
  TestBed.configureTestingModule({
    providers: [
      { provide: PLATFORM_ID, useValue: platform },
      provideSession(),
      provideSessionFreePaths((p) => p === '/vi/privacy' || p === '/terms'),
      {
        provide: SessionApi,
        useFactory: () => {
          const api = new FakeSessionApi();
          api.mode = '401';
          return api;
        },
      },
      { provide: ActivityTracker, useClass: FakeActivityTracker },
    ],
  });
  return {
    init: TestBed.inject(ApplicationInitStatus),
    api: TestBed.inject(SessionApi) as FakeSessionApi,
    store: TestBed.inject(SessionStore),
    tracker: TestBed.inject(ActivityTracker) as unknown as FakeActivityTracker,
    document: TestBed.inject(DOCUMENT),
  };
}

afterEach(() => {
  history.pushState(null, '', '/');
});

describe('provideSession', () => {
  it('restores the session once at boot in the browser and settles anonymous on a 401', async () => {
    const { init, api, store } = setup('browser');
    await init.donePromise;
    expect(api.calls).toBe(1);
    expect(store.restoreSettled()).toBe(true);
    expect(store.status()).toBe('anonymous');
    expect(store.endReason()).toBeNull();
  });

  it.each(['/vi/privacy', '/terms'])('issues no refresh at boot on the session-free path %s', async (path) => {
    const { init, api, store } = setup('browser', path);
    await init.donePromise;
    expect(api.calls).toBe(0);
    expect(store.restoreSettled()).toBe(false);
    expect(store.status()).toBe('anonymous');
  });

  it('ignores query and fragment when matching the boot path, and refreshes on other paths', async () => {
    const { init, api } = setup('browser', '/dashboard?next=/vi/privacy#/terms');
    await init.donePromise;
    expect(api.calls).toBe(1);
  });

  it('still restores exactly once, memoized, on the first guarded call after a session-free boot', async () => {
    const { init, api, store } = setup('browser', '/vi/privacy');
    await init.donePromise;
    expect(api.calls).toBe(0);

    await Promise.all([store.restore(), store.restore()]);
    expect(api.calls).toBe(1);
    expect(store.restoreSettled()).toBe(true);
  });

  it('still arms the first-gesture ActivityTracker listener on a session-free path', async () => {
    const { init, tracker, document } = setup('browser', '/vi/privacy');
    await init.donePromise;
    document.dispatchEvent(new Event('keydown'));
    expect(tracker.starts).toBe(1);
  });

  it('never refreshes on the server platform (prerender)', async () => {
    const { init, api, store, tracker, document } = setup('server');
    await init.donePromise;
    expect(api.calls).toBe(0);
    expect(store.restoreSettled()).toBe(false);
    document.dispatchEvent(new Event('pointerdown'));
    expect(tracker.starts).toBe(0);
  });

  it('starts the ActivityTracker once on the first gesture, then stops listening', async () => {
    const { init, tracker, document } = setup('browser');
    await init.donePromise;
    expect(tracker.starts).toBe(0);

    document.dispatchEvent(new Event('keydown'));
    document.dispatchEvent(new Event('pointerdown'));
    document.dispatchEvent(new Event('keydown'));
    expect(tracker.starts).toBe(1);
  });

  it('removes the first-gesture listeners when the injector is destroyed', async () => {
    const { init, tracker, document } = setup('browser');
    await init.donePromise;

    TestBed.resetTestingModule();
    document.dispatchEvent(new Event('pointerdown'));
    expect(tracker.starts).toBe(0);
  });
});
