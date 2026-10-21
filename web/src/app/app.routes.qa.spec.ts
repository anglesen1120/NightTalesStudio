import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, type Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { SessionApi, SessionStore, mfaEnrolmentGuard } from '@core/session';
import { FakeSessionApi, fakeJwt } from './core/session/testing/fake-session.api';
import { routes } from './app.routes';

// QA (10.2.6-T1): guard bypass attempts against the real shell parent's guards.

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

@Component({ imports: [RouterOutlet], template: '<router-outlet />', changeDetection: ChangeDetectionStrategy.OnPush })
class StubShell {}

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));
const fresh = () => ({ accessToken: fakeJwt('user'), expiresAt: Date.now() + 600_000 });

function harnessRoutes(): Routes {
  const parent = routes.find((route) => route.path === '');
  if (parent === undefined) throw new Error('shell parent missing');
  return [
    { path: 'login', component: StubPage },
    { path: 'account/security/mfa-setup', component: StubPage, canActivate: [mfaEnrolmentGuard] },
    {
      path: '',
      component: StubShell,
      canActivate: parent.canActivate,
      canActivateChild: parent.canActivateChild,
      children: [
        { path: 'probe-a', component: StubPage },
        { path: 'probe-b', component: StubPage },
      ],
    },
  ];
}

function setup(mode: FakeSessionApi['mode']) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter(harnessRoutes()),
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
  return { store: TestBed.inject(SessionStore), router: TestBed.inject(Router) };
}

describe('AppRoutes shell guards (QA bypass attempts)', () => {
  it('after the session ends, child-to-child navigation never lands on the second child', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.signIn(fresh(), 'user', []);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-a');
    store.signOut('expired');
    await tick();
    for (const target of ['/probe-b', '/probe-a', '/probe-b?x=1']) {
      await harness.navigateByUrl(target);
      expect(router.url.startsWith('/login')).toBe(true);
    }
  });

  it('an enrolment session deep-linking into any shell child is redirected to mfa-setup, never activated', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.beginEnrolment('enrolment-token');
    const harness = await RouterTestingHarness.create();
    for (const target of ['/probe-a', '/probe-b', '/']) {
      await harness.navigateByUrl(target);
      expect(router.url).toBe('/account/security/mfa-setup');
    }
  });

  it('a hostile returnUrl-bearing deep link is stored only as a same-origin path', async () => {
    const { store, router } = setup('401');
    await store.restore();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-a?next=//evil.example');
    const tree = router.parseUrl(router.url);
    const returnUrl = tree.queryParams['returnUrl'] as string;
    expect(returnUrl.startsWith('/')).toBe(true);
    expect(returnUrl.startsWith('//')).toBe(false);
    expect(returnUrl).not.toContain('://');
  });

  it('an anonymous visitor never activates the shell parent (root path)', async () => {
    const { store, router } = setup('401');
    await store.restore();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/');
    expect(router.url.startsWith('/login')).toBe(true);
  });

  it('a refreshing session is not treated as signed out when navigating between children', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.signIn(fresh(), 'user', []);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-a');
    await harness.navigateByUrl('/probe-b');
    expect(router.url).toBe('/probe-b');
  });
});
