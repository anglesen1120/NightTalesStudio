import { TestBed } from '@angular/core/testing';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { REFRESH_LOCK_NAME, RefreshLock } from './refresh-lock';

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: Error) => void } {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function lock(): RefreshLock {
  TestBed.configureTestingModule({ providers: [...ZONELESS_TEST_PROVIDERS] });
  return TestBed.inject(RefreshLock);
}

afterEach(() => {
  Reflect.deleteProperty(navigator, 'locks');
});

describe('RefreshLock', () => {
  describe('without navigator.locks (in-process mutex)', () => {
    it('RefreshLock_Fallback_RunsOneAtATimeInOrder', async () => {
      const refreshLock = lock();
      expect('locks' in navigator).toBe(false);
      const events: string[] = [];
      const first = deferred<string>();

      const a = refreshLock.run(async () => {
        events.push('a:start');
        const value = await first.promise;
        events.push('a:end');
        return value;
      });
      const b = refreshLock.run(() => {
        events.push('b:start');
        return Promise.resolve('b');
      });

      await Promise.resolve();
      await Promise.resolve();
      expect(events).toEqual(['a:start']);

      first.resolve('a');
      await expect(Promise.all([a, b])).resolves.toEqual(['a', 'b']);
      expect(events).toEqual(['a:start', 'a:end', 'b:start']);
    });

    it('RefreshLock_Fallback_FailureDoesNotPoisonTheQueue', async () => {
      const refreshLock = lock();
      const failing = refreshLock.run(() => Promise.reject(new Error('refresh failed')));
      const next = refreshLock.run(() => Promise.resolve('ok'));

      await expect(failing).rejects.toThrow('refresh failed');
      await expect(next).resolves.toBe('ok');
    });
  });

  describe('with navigator.locks', () => {
    it('RefreshLock_WebLocks_RequestsTheNamedExclusiveLock', async () => {
      const requests: { name: string; options: unknown }[] = [];
      Object.defineProperty(navigator, 'locks', {
        configurable: true,
        value: {
          request: (name: string, options: unknown, callback: () => Promise<unknown>) => {
            requests.push({ name, options });
            return callback();
          },
        },
      });
      const refreshLock = lock();

      await expect(refreshLock.run(() => Promise.resolve(42))).resolves.toBe(42);
      expect(requests).toEqual([{ name: REFRESH_LOCK_NAME, options: { mode: 'exclusive' } }]);
      expect(REFRESH_LOCK_NAME).toBe('nts-refresh');
    });

    it('RefreshLock_WebLocks_PropagatesTheCallbackFailure', async () => {
      Object.defineProperty(navigator, 'locks', {
        configurable: true,
        value: { request: (_name: string, _options: unknown, callback: () => Promise<unknown>) => callback() },
      });
      const refreshLock = lock();

      await expect(refreshLock.run(() => Promise.reject(new Error('nope')))).rejects.toThrow('nope');
    });
  });
});
