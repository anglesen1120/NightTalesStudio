import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { AppErrorMapper } from '@core/errors';
import { anonymousGuard, authChildGuard, authGuard, mfaEnrolmentGuard } from './auth.guard';
import { SessionApi } from './session.api';
import { SessionStore } from './session-store';
import { FakeSessionApi, fakeJwt } from './testing/fake-session.api';
import type { TokenPair } from './token-pair';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

const routes: Routes = [
  { path: 'login', component: StubPage, canActivate: [anonymousGuard] },
  { path: 'dashboard', component: StubPage, canActivate: [authGuard] },
  { path: 'account/security/mfa-setup', component: StubPage, canActivate: [mfaEnrolmentGuard] },
  {
    path: 'app',
    canActivateChild: [authChildGuard],
    children: [
      { path: 'a', component: StubPage },
      { path: 'b', component: StubPage },
    ],
  },
];

function freshTokens(role: 'user' | 'admin' | 'owner' = 'user'): TokenPair {
  return { accessToken: fakeJwt(role), expiresAt: Date.now() + 600_000 };
}

function setup(mode: FakeSessionApi['mode']) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(routes),
      {
        provide: SessionApi,
        useFactory: () => {
          const api = new FakeSessionApi();
          api.mode = mode;
          return api;
        },
      },
    ],
  });
  return {
    api: TestBed.inject(SessionApi) as FakeSessionApi,
    store: TestBed.inject(SessionStore),
    router: TestBed.inject(Router),
  };
}

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

function loginReturnUrl(router: Router): string | undefined {
  const tree = router.parseUrl(router.url);
  expect(tree.root.children['primary']?.segments.map((s) => s.path)).toEqual(['login']);
  const value: unknown = tree.queryParams['returnUrl'];
  return typeof value === 'string' ? value : undefined;
}

describe('AuthGuard_WaitsForRestore_ThenRedirects', () => {
  it('neither activates nor redirects while the restore is pending, then activates once it resolves', async () => {
    const { api, router } = setup('deferred');
    const harness = await RouterTestingHarness.create();

    let settled = false;
    const navigation = harness.navigateByUrl('/dashboard').then(() => {
      settled = true;
    });
    await tick();
    expect(settled).toBe(false);
    expect(router.url).toBe('/');
    expect(api.calls).toBe(1);

    api.resolveNext(freshTokens());
    await navigation;
    expect(router.url).toBe('/dashboard');
    expect(api.calls).toBe(1);
  });

  it('redirects an anonymous visitor to /login with the sanitized returnUrl after the restore settles', async () => {
    const { api, router } = setup('deferred');
    const harness = await RouterTestingHarness.create();

    const navigation = harness.navigateByUrl('/dashboard');
    await tick();
    expect(router.url).toBe('/');

    api.rejectNext(
      TestBed.inject(AppErrorMapper).from(new HttpErrorResponse({ status: 401, error: { code: 'unauthorized' } })),
    );
    await navigation;
    expect(loginReturnUrl(router)).toBe('/dashboard');
  });

  it('redirects with a 401 restore, keeping endReason null', async () => {
    const { store, router } = setup('401');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/dashboard');
    expect(loginReturnUrl(router)).toBe('/dashboard');
    expect(store.endReason()).toBeNull();
    expect(store.restoreSettled()).toBe(true);
  });
});

describe('AnonymousGuard_Authenticated_RedirectsToDashboard', () => {
  it('sends a full session away from /login to /dashboard', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.signIn(freshTokens(), 'user', []);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/login');
    expect(router.url).toBe('/dashboard');
  });

  it('sends an enrolment session to /account/security/mfa-setup', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.beginEnrolment('enrolment-token');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/login');
    expect(router.url).toBe('/account/security/mfa-setup');
  });

  it('lets an anonymous visitor and an ended session see /login', async () => {
    const { store, router } = setup('401');
    await store.restore();
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/login');
    expect(router.url).toBe('/login');

    store.signIn(freshTokens(), 'user', []);
    store.signOut('expired');
    await tick();
    await harness.navigateByUrl('/login');
    expect(router.parseUrl(router.url).root.children['primary']?.segments[0]?.path).toBe('login');
    expect(store.endReason()).toBe('expired');
  });
});

describe('AuthChildGuard_RechecksSession_SeparatesEnrolment', () => {
  it('waits for a pending restore before activating a child', async () => {
    const { api, router } = setup('deferred');
    const harness = await RouterTestingHarness.create();

    let settled = false;
    const navigation = harness.navigateByUrl('/app/a').then(() => {
      settled = true;
    });
    await tick();
    expect(settled).toBe(false);
    expect(router.url).toBe('/');

    api.resolveNext(freshTokens());
    await navigation;
    expect(router.url).toBe('/app/a');
  });

  it('re-evaluates on child-to-child navigation: an ended session cannot activate the next child', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.signIn(freshTokens(), 'user', []);
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/app/a');
    expect(router.url).toBe('/app/a');

    store.signOut('revoked');
    await tick();
    await harness.navigateByUrl('/app/b');
    expect(loginReturnUrl(router)).toBe('/app/b');
    expect(store.endReason()).toBe('revoked');
  });

  it('sends an enrolment-only session to the setup page instead of the full-session shell', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.beginEnrolment('enrolment-token');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/app/a');
    expect(router.url).toBe('/account/security/mfa-setup');
    await harness.navigateByUrl('/dashboard');
    expect(router.url).toBe('/account/security/mfa-setup');
  });
});

describe('MfaEnrolmentGuard', () => {
  it('allows an enrolment-only session and a full session on /account/security/mfa-setup', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.beginEnrolment('enrolment-token');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/account/security/mfa-setup');
    expect(router.url).toBe('/account/security/mfa-setup');

    store.signIn(freshTokens('admin'), 'admin', []);
    await harness.navigateByUrl('/login');
    expect(router.url).toBe('/dashboard');
    await harness.navigateByUrl('/account/security/mfa-setup');
    expect(router.url).toBe('/account/security/mfa-setup');
  });

  it('redirects an anonymous visitor to /login with the setup returnUrl', async () => {
    const { router } = setup('401');
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/account/security/mfa-setup');
    expect(loginReturnUrl(router)).toBe('/account/security/mfa-setup');
  });
});
