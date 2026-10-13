import { ApplicationRef, ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Title } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { LocaleService, type AppLocale } from '@core/i18n';
import { RealtimeService } from '@core/realtime';
import { MeRepository, SessionApi, SessionStore } from '@core/session';
import { FakeMeRepository, fakeMeSummary } from '../session/testing/fake-me.repository';
import { FakeSessionApi, fakeJwt } from '../session/testing/fake-session.api';
import { ZONELESS_TEST_PROVIDERS } from '../../testing/providers';
import { NAV_ITEMS_TOKEN, type NavItem } from './nav-items';
import { ShellStore } from './shell.store';
import { UnsavedWorkRegistry } from './unsaved-work';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class Page {}

class FakeLocaleService {
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

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

async function setup(options: { signedIn?: boolean; mode?: 'full' | 'enrolment'; navItems?: readonly NavItem[] } = {}) {
  const locale = new FakeLocaleService();
  const realtime = new FakeRealtime();
  const snackOpen = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'a', component: Page, data: { titleKey: 'shell.title.dashboard' } },
        { path: 'b', component: Page },
        { path: 'login', component: Page },
      ]),
      { provide: SessionApi, useClass: FakeSessionApi },
      { provide: MeRepository, useClass: FakeMeRepository },
      { provide: LocaleService, useValue: locale },
      { provide: RealtimeService, useValue: realtime },
      { provide: MatSnackBar, useValue: { open: snackOpen } },
      {
        provide: TranslocoService,
        useValue: { selectTranslate: (key: string) => of(`${locale.current()}:${key}`) },
      },
      { provide: NAV_ITEMS_TOKEN, useValue: options.navItems ?? [] },
      ShellStore,
    ],
  });
  const session = TestBed.inject(SessionStore);
  // `ok` mode restores a session at boot, so an anonymous visitor needs the 401 restore.
  if (options.signedIn === false) (TestBed.inject(SessionApi) as FakeSessionApi).mode = '401';
  await session.restore();
  if (options.signedIn !== false) {
    if (options.mode === 'enrolment') session.beginEnrolment('enrolment-token');
    else session.signIn({ accessToken: fakeJwt('user'), expiresAt: Date.now() + 600_000 }, 'user', []);
  }
  return {
    store: TestBed.inject(ShellStore),
    session,
    registry: TestBed.inject(UnsavedWorkRegistry),
    repo: TestBed.inject(MeRepository) as FakeMeRepository,
    locale,
    realtime,
    snackOpen,
    router: TestBed.inject(Router),
    title: TestBed.inject(Title),
  };
}

describe('ShellStore', () => {
  it('loadMe_FetchesOnceAndSetsSummary', async () => {
    const { store, session, repo } = await setup();
    await store.loadMe();
    expect(repo.getCalls).toBe(1);
    expect(session.me()).toEqual(repo.profile);
    expect(store.summary()).toEqual(repo.profile);
  });

  it('loadMe_IsSkippedWhenSummaryPresent', async () => {
    const { store, session, repo } = await setup();
    session.setMe(fakeMeSummary());
    await store.loadMe();
    expect(repo.getCalls).toBe(0);
  });

  it('loadMe_ConcurrentCallsIssueOneRequest', async () => {
    const { store, repo } = await setup();
    repo.deferred = true;
    const first = store.loadMe();
    const second = store.loadMe();
    expect(repo.getCalls).toBe(1);
    repo.resolveNextGet();
    await Promise.all([first, second]);
    expect(repo.getCalls).toBe(1);
    expect(store.summary()).not.toBeNull();
  });

  it('loadMe_SkippedInEnrolmentMode', async () => {
    const { store, repo } = await setup({ mode: 'enrolment' });
    await store.loadMe();
    expect(repo.getCalls).toBe(0);
  });

  it('loadMe_ProfileErrorKeepsNavigationUsable', async () => {
    const entry: NavItem = { id: 'x', labelKey: 'k', route: '/a', icon: Page, visible: () => true };
    const { store, repo, session } = await setup({ navItems: [entry] });
    repo.failure = FakeMeRepository.serverError();
    await store.loadMe();
    expect(store.profileError()?.status).toBe(503);
    expect(session.me()).toBeNull();
    expect(store.navItems()).toEqual([entry]);
    await store.retryProfile();
    expect(store.profileError()).toBeNull();
    expect(repo.getCalls).toBe(2);
    expect(store.summary()).not.toBeNull();
  });

  it('loadMe_UnauthorizedDoesNotSetProfileError', async () => {
    const { store, repo } = await setup();
    repo.failure = FakeMeRepository.unauthorized();
    await store.loadMe();
    expect(store.profileError()).toBeNull();
  });

  it('loadMe_ResultOfAnEndedSessionIsDiscarded', async () => {
    const { store, repo, session } = await setup();
    repo.deferred = true;
    const pending = store.loadMe();
    session.signOut('expired');
    repo.resolveNextGet();
    await pending;
    expect(session.me()).toBeNull();
  });

  it('summary_MapsMissingFeaturesToEmptyArray', async () => {
    const { store, repo } = await setup();
    repo.profile = fakeMeSummary({ features: [] });
    await store.loadMe();
    expect(store.summary()?.features).toEqual([]);
  });

  it('changeLocale_AppliesLocallyAndPersistsOnce', async () => {
    const { store, repo, locale, session } = await setup();
    await store.changeLocale('en');
    expect(locale.activations).toEqual([{ locale: 'en', persist: true }]);
    expect(repo.patchedLocales).toEqual(['en']);
    expect(session.me()?.uiLocale).toBe('en');
    expect(store.localeSaving()).toBe(false);
  });

  it('changeLocale_WhenAnonymous_DoesNotCallApi', async () => {
    const { store, repo, locale } = await setup({ signedIn: false });
    await store.changeLocale('en');
    expect(locale.activations).toEqual([{ locale: 'en', persist: true }]);
    expect(repo.patchCalls).toBe(0);
  });

  it('changeLocale_ApiFailureKeepsLocaleAndShowsSnackbar', async () => {
    const { store, repo, locale, snackOpen } = await setup();
    repo.failure = FakeMeRepository.serverError();
    await store.changeLocale('en');
    expect(locale.current()).toBe('en');
    expect(snackOpen).toHaveBeenCalledTimes(1);
    expect(snackOpen.mock.calls[0]?.[0]).toBe('en:shell.language.saveFailed');
  });

  it('changeLocale_ReentrantClickIssuesOneRequest', async () => {
    const { store, repo, locale } = await setup();
    repo.deferred = true;
    const first = store.changeLocale('en');
    const second = store.changeLocale('vi');
    await second;
    expect(repo.patchCalls).toBe(1);
    expect(locale.activations).toEqual([{ locale: 'en', persist: true }]);
    repo.resolveNextPatch();
    await first;
    expect(repo.patchCalls).toBe(1);
  });

  it('logOut_DelegatesToSessionStore', async () => {
    const { store, session } = await setup();
    const logout = vi.spyOn(session, 'logout');
    store.logOut();
    store.logOut();
    expect(logout).toHaveBeenCalledTimes(1);
  });

  it('logOut_Stay_AbortsTheLogout_AndAsksAgainNextTime', async () => {
    const { store, session, registry } = await setup();
    const logout = vi.spyOn(session, 'logout');
    const ask = vi.fn(() => Promise.resolve(false));
    registry.register({ hasUnsaved: () => true, discard: () => undefined, confirmLeave: ask });
    store.logOut();
    await tick();
    expect(ask).toHaveBeenCalledTimes(1);
    expect(logout).not.toHaveBeenCalled();
    expect(session.status()).toBe('authenticated');
    store.logOut();
    await tick();
    expect(ask).toHaveBeenCalledTimes(2);
    expect(logout).not.toHaveBeenCalled();
  });

  it('logOut_Leave_EndsTheSessionAfterThePrompt', async () => {
    const { store, session, registry } = await setup();
    const logout = vi.spyOn(session, 'logout');
    const ask = vi.fn(() => Promise.resolve(true));
    registry.register({ hasUnsaved: () => true, discard: () => undefined, confirmLeave: ask });
    store.logOut();
    await tick();
    expect(ask).toHaveBeenCalledTimes(1);
    expect(logout).toHaveBeenCalledTimes(1);
    expect(session.status()).toBe('ended');
  });

  it('terminalSessionEnd_NeverPrompts_ButDiscardsUnsavedWork', async () => {
    const { session, registry } = await setup();
    const check = vi.fn(() => true);
    const ask = vi.fn(() => Promise.resolve(false));
    const discard = vi.fn();
    registry.register({ hasUnsaved: check, discard, confirmLeave: ask });
    session.signOut('revoked');
    expect(session.status()).toBe('ended');
    expect(session.endReason()).toBe('revoked');
    expect(check).not.toHaveBeenCalled();
    expect(ask).not.toHaveBeenCalled();
    expect(discard).toHaveBeenCalledTimes(1);
  });

  it('resumeRealtime_DelegatesToRealtimeService', async () => {
    const { store, realtime } = await setup();
    store.resumeRealtime();
    expect(realtime.resumes).toBe(1);
  });

  it('pageTitle_UsesShellTitleKeyAndFollowsLocaleChange', async () => {
    const { store, router, title, locale } = await setup();
    TestBed.runInInjectionContext(() => store.init());
    await router.navigateByUrl('/a');
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(store.pageTitle()).toBe('vi:shell.title.dashboard');
    expect(title.getTitle()).toBe('vi:shell.title.dashboard');
    locale.activate('en', { persist: false });
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(store.pageTitle()).toBe('en:shell.title.dashboard');
    expect(title.getTitle()).toBe('en:shell.title.dashboard');
  });

  it('profileLocale_IsActivatedWithoutPersistUnlessUserChoseLocally', async () => {
    const first = await setup();
    // `init()` starts the first `loadMe()`, so the server profile must be scripted before it.
    first.repo.profile = fakeMeSummary({ uiLocale: 'en' });
    TestBed.runInInjectionContext(() => first.store.init());
    await first.store.loadMe();
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(first.locale.activations).toContainEqual({ locale: 'en', persist: false });
  });

  it('init_LoadsTheProfileOnceWhenASessionRefreshingAtMountBecomesAuthenticated', async () => {
    const { store, session, repo } = await setup({ signedIn: false });
    const api = TestBed.inject(SessionApi) as FakeSessionApi;
    api.mode = 'deferred';
    session.signIn({ accessToken: fakeJwt('user'), expiresAt: Date.now() + 1_000 }, 'user', []);
    const flight = session.ensureFreshAccessToken({ userAction: true });
    await tick();
    expect(session.status()).toBe('refreshing');
    TestBed.runInInjectionContext(() => store.init());
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(repo.getCalls).toBe(0);
    api.resolveNext({ accessToken: fakeJwt('user'), expiresAt: Date.now() + 600_000 });
    await flight;
    await TestBed.inject(ApplicationRef).whenStable();
    await tick();
    expect(session.status()).toBe('authenticated');
    expect(repo.getCalls).toBe(1);
    expect(session.me()).toEqual(repo.profile);
  });
});
