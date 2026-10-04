import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter, type Route, type Routes } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { describe, expect, it } from 'vitest';
import { SessionApi, SessionStore, authChildGuard, authGuard, mfaEnrolmentGuard } from '@core/session';
import { FakeSessionApi, fakeJwt } from './core/session/testing/fake-session.api';
import { routes } from './app.routes';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

@Component({ imports: [RouterOutlet], template: '<router-outlet />', changeDetection: ChangeDetectionStrategy.OnPush })
class StubShell {}

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));
const fresh = () => ({ accessToken: fakeJwt('user'), expiresAt: Date.now() + 600_000 });

/** The shell parent is the one entry guarded by authGuard (sibling public/auth routes from later tasks are not). */
function shellParent(): Route {
  const parent = routes.find((route) => route.canActivate?.includes(authGuard) === true && typeof route.loadComponent === 'function');
  if (parent === undefined) throw new Error('shell parent missing');
  return parent;
}

/** The real shell parent's guards around a stub component and a test-only child (the real table has no children yet). */
function harnessRoutes(): Routes {
  const parent = shellParent();
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
  return { api: TestBed.inject(SessionApi) as FakeSessionApi, store: TestBed.inject(SessionStore), router: TestBed.inject(Router) };
}

function onLogin(router: Router): boolean {
  return router.parseUrl(router.url).root.children['primary']?.segments[0]?.path === 'login';
}

describe('AppRoutes_RouteTable', () => {
  it('has exactly one shell parent guarded by authGuard/authChildGuard with an empty children array', () => {
    const parents = routes.filter((route) => route.canActivate?.includes(authGuard) === true && typeof route.loadComponent === 'function');
    expect(parents).toHaveLength(1);
    const parent = shellParent();
    expect(parent.path).toBe('');
    expect(parent.canActivate).toEqual([authGuard]);
    expect(parent.canActivateChild).toEqual([authChildGuard]);
    expect(parent.children).toEqual([]);
    expect(typeof parent.loadComponent).toBe('function');
  });

  it('declares no stub routes: no wildcard, no /dashboard', () => {
    const paths = routes.map((route) => route.path);
    expect(paths).not.toContain('**');
    expect(paths).not.toContain('dashboard');
    expect(shellParent().children).toHaveLength(0);
  });
});

describe('AppRoutes_ShellGuards', () => {
  it('a pending restore prevents activation until it resolves', async () => {
    const { api, router } = setup('deferred');
    const harness = await RouterTestingHarness.create();
    let settled = false;
    const navigation = harness.navigateByUrl('/probe-a').then(() => {
      settled = true;
    });
    await tick();
    expect(settled).toBe(false);
    expect(router.url).toBe('/');
    api.resolveNext(fresh());
    await navigation;
    expect(router.url).toBe('/probe-a');
  });

  it('an ended session during child navigation redirects to login', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.signIn(fresh(), 'user', []);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-a');
    expect(router.url).toBe('/probe-a');

    store.signOut('expired');
    await tick();
    await harness.navigateByUrl('/probe-b');
    expect(onLogin(router)).toBe(true);
  });

  it('an enrolment session is sent to mfa-setup and never reaches the shell child', async () => {
    const { store, router } = setup('401');
    await store.restore();
    store.beginEnrolment('enrolment-token');
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-a');
    expect(router.url).not.toBe('/probe-a');
    await harness.navigateByUrl('/account/security/mfa-setup');
    expect(router.url).toBe('/account/security/mfa-setup');
  });

  it('an unauthorized deep link redirects to login with the returnUrl and does not fall through', async () => {
    const { store, router } = setup('401');
    await store.restore();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/probe-b');
    expect(onLogin(router)).toBe(true);
    expect(router.parseUrl(router.url).queryParams['returnUrl']).toBe('/probe-b');
  });
});
