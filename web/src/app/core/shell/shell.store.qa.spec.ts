import { ApplicationRef, ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { AppError } from '@core/errors';
import { LocaleService, type AppLocale } from '@core/i18n';
import { RealtimeService } from '@core/realtime';
import { MeRepository, SessionApi, SessionStore } from '@core/session';
import { FakeMeRepository, fakeMeSummary } from '../session/testing/fake-me.repository';
import { FakeSessionApi, fakeJwt } from '../session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { NAV_ITEMS_TOKEN } from './nav-items';
import { ShellStore } from './shell.store';

// QA (10.2.6-T1): PATCH/GET failure matrix, late results after logout / user switch / shell destroy, generations.

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class Page {}

class FakeLocale {
  readonly current = signal<AppLocale>('vi');
  readonly locale = this.current.asReadonly();
  readonly activations: { locale: AppLocale; persist: boolean }[] = [];
  activate(locale: AppLocale, options: { persist: boolean }): void {
    this.activations.push({ locale, persist: options.persist });
    this.current.set(locale);
  }
}

class FakeRealtime {
  readonly state = signal('connected');
  resumes = 0;
  resume(): void {
    this.resumes += 1;
  }
}

const err = (status: number, kind = 'server'): AppError => ({ code: `e${String(status)}`, status, kind, fieldErrors: {} }) as AppError;
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

async function setup() {
  const locale = new FakeLocale();
  const snackOpen = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'login', component: Page }, { path: 'a', component: Page }]),
      { provide: SessionApi, useClass: FakeSessionApi },
      { provide: MeRepository, useClass: FakeMeRepository },
      { provide: LocaleService, useValue: locale },
      { provide: RealtimeService, useValue: new FakeRealtime() },
      { provide: MatSnackBar, useValue: { open: snackOpen } },
      { provide: TranslocoService, useValue: { selectTranslate: (key: string) => of(`${locale.current()}:${key}`) } },
      { provide: NAV_ITEMS_TOKEN, useValue: [] },
      ShellStore,
    ],
  });
  const session = TestBed.inject(SessionStore);
  await session.restore();
  const signIn = (role: 'user' | 'admin' = 'user'): void =>
    session.signIn({ accessToken: fakeJwt(role), expiresAt: Date.now() + 600_000 }, role, []);
  signIn();
  return {
    store: TestBed.inject(ShellStore),
    session,
    repo: TestBed.inject(MeRepository) as FakeMeRepository,
    locale,
    snackOpen,
    signIn,
    router: TestBed.inject(Router),
  };
}

describe('ShellStore (QA)', () => {
  it.each([400, 403, 404, 422, 500, 502, 503, 0])('PATCH failure %i keeps the locale and shows exactly one snackbar', async (status) => {
    const { store, repo, locale, snackOpen, session } = await setup();
    repo.failure = err(status);
    await store.changeLocale('en');
    expect(locale.current()).toBe('en');
    expect(snackOpen).toHaveBeenCalledTimes(1);
    expect(store.localeSaving()).toBe(false);
    expect(session.status()).toBe('authenticated');
    // The UI is usable again: a second switch issues a new request.
    await store.changeLocale('vi');
    expect(repo.patchCalls).toBe(2);
  });

  it('a PATCH 401 does not show the save-failed snackbar (session policy owns it)', async () => {
    const { store, repo, snackOpen } = await setup();
    repo.failure = FakeMeRepository.unauthorized();
    await store.changeLocale('en');
    expect(snackOpen).not.toHaveBeenCalled();
  });

  it('a non-AppError rejection (raw Error) is mapped and still shows one snackbar', async () => {
    const { store, repo, snackOpen } = await setup();
    repo.updateUiLocale = (): Promise<never> => Promise.reject(new Error('offline'));
    await store.changeLocale('en');
    expect(snackOpen).toHaveBeenCalledTimes(1);
  });

  it('clicking the same locale that is already active still behaves (one request, no snackbar on success)', async () => {
    const { store, repo, snackOpen } = await setup();
    await store.changeLocale('vi');
    expect(repo.patchCalls).toBe(1);
    expect(snackOpen).not.toHaveBeenCalled();
  });

  it('late PATCH success after logout is discarded: me stays null', async () => {
    const { store, repo, session } = await setup();
    repo.deferred = true;
    const pending = store.changeLocale('en');
    session.signOut('logout');
    repo.resolveNextPatch(fakeMeSummary({ uiLocale: 'en' }));
    await pending;
    expect(session.me()).toBeNull();
    expect(store.localeSaving()).toBe(false);
  });

  it('late PATCH failure after logout shows no snackbar', async () => {
    const { store, repo, session, snackOpen } = await setup();
    repo.deferred = true;
    const pending = store.changeLocale('en');
    session.signOut('logout');
    repo.rejectNextPatch(err(500));
    await pending;
    expect(snackOpen).not.toHaveBeenCalled();
  });

  it('late PATCH success for user A after user B signs in never lands on B', async () => {
    const { store, repo, session, signIn } = await setup();
    repo.deferred = true;
    const pending = store.changeLocale('en');
    session.signOut('logout');
    signIn('admin');
    session.setMe(fakeMeSummary({ id: 'B', displayName: 'Bee' }));
    repo.resolveNextPatch(fakeMeSummary({ id: 'A', uiLocale: 'en' }));
    await pending;
    expect(session.me()?.id).toBe('B');
  });

  it('late GET for user A after user B signs in is discarded; B loads its own profile', async () => {
    const { store, repo, session, signIn } = await setup();
    repo.deferred = true;
    const first = store.loadMe();
    session.signOut('logout');
    signIn('admin');
    const second = store.loadMe();
    expect(repo.getCalls).toBe(2);
    repo.resolveNextGet(fakeMeSummary({ id: 'A' }));
    await first;
    expect(session.me()).toBeNull();
    repo.resolveNextGet(fakeMeSummary({ id: 'B' }));
    await second;
    expect(session.me()?.id).toBe('B');
  });

  it('late GET failure after logout does not set profileError', async () => {
    const { store, repo, session } = await setup();
    repo.deferred = true;
    const pending = store.loadMe();
    session.signOut('expired');
    repo.rejectNextGet(err(500));
    await pending;
    expect(store.profileError()).toBeNull();
  });

  it('late GET/PATCH results after the shell (store) is destroyed change nothing', async () => {
    const { store, repo, session, snackOpen } = await setup();
    repo.deferred = true;
    const get = store.loadMe();
    const patch = store.changeLocale('en');
    TestBed.resetTestingModule(); // destroys the root injector and therefore the store's DestroyRef
    repo.resolveNextGet(fakeMeSummary({ id: 'late' }));
    repo.rejectNextPatch(err(500));
    await Promise.all([get, patch]);
    expect(session.me()).toBeNull();
    expect(snackOpen).not.toHaveBeenCalled();
    expect(store.profileError()).toBeNull();
  });

  it('loadMe after destroy is a no-op (no request)', async () => {
    const { store, repo } = await setup();
    TestBed.resetTestingModule();
    await store.loadMe();
    expect(repo.getCalls).toBe(0);
  });

  it('a late /me (older than the user\'s own PATCH) neither overrides the chosen locale nor the persisted me.uiLocale', async () => {
    const { store, repo, locale, session } = await setup();
    repo.deferred = true;
    TestBed.runInInjectionContext(() => store.init()); // starts one deferred GET
    const patch = store.changeLocale('en');
    expect(locale.current()).toBe('en');
    repo.resolveNextPatch(fakeMeSummary({ uiLocale: 'en' }));
    await patch;
    repo.resolveNextGet(fakeMeSummary({ uiLocale: 'vi' })); // server snapshot taken before the PATCH
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(locale.current()).toBe('en');
    // Expected: the stale GET must not clobber the newer PATCH result (me.uiLocale stays 'en').
    expect(session.me()?.uiLocale).toBe('en');
  });

  it('retryProfile while a retry is already in flight issues one request', async () => {
    const { store, repo } = await setup();
    repo.failure = err(503);
    await store.loadMe();
    repo.deferred = true;
    const a = store.retryProfile();
    const b = store.retryProfile();
    expect(repo.getCalls).toBe(2);
    repo.resolveNextGet();
    await Promise.all([a, b]);
    expect(repo.getCalls).toBe(2);
  });

  it('logOut twice quickly calls logout once, and a failing logout does not throw or wedge', async () => {
    const { store, session } = await setup();
    const logout = vi.spyOn(session, 'logout').mockRejectedValueOnce(new Error('net'));
    store.logOut();
    store.logOut();
    await tick();
    expect(logout).toHaveBeenCalledTimes(1);
    logout.mockResolvedValue(undefined);
    store.logOut();
    expect(logout).toHaveBeenCalledTimes(2);
  });
});
