import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '@core/session';
import { EpisodesRepository, type EpisodeStatus } from '@features/episodes';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import dashboardEn from '../../../../assets/i18n/dashboard/en.json';
import dashboardVi from '../../../../assets/i18n/dashboard/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { DASHBOARD_STATUSES } from '../dashboard.model';
import { FakeDashboardEpisodesRepository, dashboardEpisode } from '../testing/fake-episode.repository';
import { DashboardPage } from './dashboard-page';

// QA (10.4.1-T1): adversarial page paths the shipped spec does not cover — a Retry-After of N disabling Retry
// until the countdown elapses, a hidden focus refresh issuing nothing, rapid focus+visibility bursts coalescing
// to one batch, listeners removed on teardown, a session generation change mid-load painting no error, an
// unknown wire status rendering no row, an untitled row's accessible name, and the no-credit copy contract of
// BOTH i18n files (the e2e regex only reads the rendered page, never the source JSON).

const translations = TranslocoTestingModule.forRoot({
  langs: {
    'dashboard/vi': dashboardVi,
    'episodes/vi': episodesVi,
    'errors/vi': errorsVi,
  },
  translocoConfig: {
    availableLangs: ['vi'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

const sessionStub = {
  generation: signal(0),
  role: signal('owner'),
  hasFeature: () => true,
};

let originalVisibilityDescriptor: PropertyDescriptor | undefined;

function setVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: state });
}

async function flush(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let round = 0; round < 4; round++) await Promise.resolve();
  fixture.detectChanges();
  await fixture.whenStable();
}

async function setup(
  repository = new FakeDashboardEpisodesRepository(),
  visibility: DocumentVisibilityState = 'visible',
) {
  setVisibility(visibility);
  await TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'dashboard', component: DashboardPage },
        { path: 'episodes', component: StubPage },
      ]),
      { provide: EpisodesRepository, useValue: repository },
      { provide: SessionStore, useValue: sessionStub },
    ],
  }).compileComponents();
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/dashboard', DashboardPage);
  await flush(harness.fixture);
  return { harness, repository, root: harness.routeNativeElement as HTMLElement };
}

beforeAll(() => {
  originalVisibilityDescriptor = Object.getOwnPropertyDescriptor(document, 'visibilityState');
});

afterEach(() => {
  vi.useRealTimers();
  sessionStub.generation.set(0);
  if (originalVisibilityDescriptor !== undefined) {
    Object.defineProperty(document, 'visibilityState', originalVisibilityDescriptor);
  } else {
    setVisibility('visible');
  }
  TestBed.resetTestingModule();
});

describe('DashboardPage_QA_RateLimit', () => {
  it('Retry_Disabled_UntilCountdownElapses', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const repository = new FakeDashboardEpisodesRepository();
    repository.failStatus('ready', {
      code: 'rate_limited',
      status: 429,
      kind: 'rate_limit',
      fieldErrors: {},
      retryAfterSec: 2,
    });
    const { harness, root } = await setup(repository);
    const retryButton = root.querySelector<HTMLButtonElement>('.dashboard-page__retry button');
    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(retryButton?.disabled, 'Retry stays disabled while the countdown runs').toBe(true);
    repository.calls.splice(0);
    retryButton?.click();
    await flush(harness.fixture);
    expect(repository.calls, 'a disabled Retry issues nothing').toHaveLength(0);
  });

  it('NoCreditText_InEitherLanguage', () => {
    // The i18n contract itself: neither dashboard copy file may carry credit/balance/quote/buy text (D14).
    const forbidden = /\b(credits?|balance|quote|buy|purchase|top[- ]?up)\b|tín dụng|số dư|mua|nạp/i;
    const flatten = (value: unknown): string[] =>
      typeof value === 'string'
        ? [value]
        : Object.values(value as Record<string, unknown>).flatMap(flatten);
    for (const file of [dashboardVi, dashboardEn]) {
      for (const leaf of flatten(file)) expect(leaf).not.toMatch(forbidden);
    }
  });
});

describe('DashboardPage_QA_Focus', () => {
  it('FocusRefresh_WhileHidden_IssuesNoRequest', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const { harness, repository } = await setup();
    expect(repository.calls).toHaveLength(DASHBOARD_STATUSES.length);

    setVisibility('hidden');
    vi.setSystemTime(new Date('2026-10-05T10:00:20Z'));
    window.dispatchEvent(new Event('focus'));
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(harness.fixture);
    expect(repository.calls, 'focus while hidden issues no refresh').toHaveLength(DASHBOARD_STATUSES.length);

    setVisibility('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(harness.fixture);
    expect(repository.calls, 'the return to visible triggers exactly one batch').toHaveLength(
      DASHBOARD_STATUSES.length * 2,
    );
  });

  it('RapidFocusAndVisibilityEvents_CoalesceToOneBatch', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const { harness, repository } = await setup();
    vi.setSystemTime(new Date('2026-10-05T10:00:20Z'));
    // A burst of attention events in the same tick: one refresh, not one per event.
    window.dispatchEvent(new Event('focus'));
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('focus'));
    window.dispatchEvent(new Event('focus'));
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(harness.fixture);
    expect(repository.calls).toHaveLength(DASHBOARD_STATUSES.length * 2);
  });

  it('ListenersRemoved_WhenLeavingTheRoute_NoRefreshAfterDestroy', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const { harness, repository } = await setup();
    expect(repository.calls).toHaveLength(DASHBOARD_STATUSES.length);

    await harness.navigateByUrl('/episodes');
    await flush(harness.fixture);
    vi.setSystemTime(new Date('2026-10-05T10:00:30Z'));
    window.dispatchEvent(new Event('focus'));
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(harness.fixture);
    expect(repository.calls, 'no dashboard refresh after the page is gone').toHaveLength(DASHBOARD_STATUSES.length);
  });
});

describe('DashboardPage_QA_Staleness', () => {
  it('SessionEnd_WhileLoading_PaintsNoError', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.defer();
    const { harness, root } = await setup(repository);
    expect(root.querySelectorAll('nts-loading-state')).toHaveLength(3);
    // The session generation moved on mid-load (logout / sign-out): the late answer commits nothing and
    // the failure path paints no error state.
    sessionStub.generation.set(9);
    repository.resolveAll();
    await flush(harness.fixture);
    expect(root.querySelector('nts-error-state')).toBeNull();
    expect(repository.calls).toHaveLength(DASHBOARD_STATUSES.length);
  });

  it('LateFailure_WhileLoading_PaintsNoError', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.defer();
    const { harness, root } = await setup(repository);
    sessionStub.generation.set(9);
    repository.rejectAll({ code: 'internal_error', status: 500, kind: 'server', fieldErrors: {} });
    await flush(harness.fixture);
    expect(root.querySelector('nts-error-state')).toBeNull();
    expect(root.querySelector('.dashboard-page__refresh-error')).toBeNull();
  });
});

describe('DashboardPage_QA_Content', () => {
  it('UnknownWireStatus_RendersNoRow', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.withStatus('story_review', [
      dashboardEpisode(1, { status: 'story_review' }),
      dashboardEpisode(9, { status: 'mystery_status' as EpisodeStatus, title: 'Mystery' }),
    ]);
    const { root } = await setup(repository);
    expect(root.querySelectorAll('li.next-action-row')).toHaveLength(1);
    expect(root.textContent).not.toContain('Mystery');
    expect(root.querySelector('.dashboard-group__count')?.textContent?.trim()).toBe('1');
  });

  it('UntitledRow_AccessibleName_UsesTheUntitledLabel', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.withStatus('story_review', [dashboardEpisode(1, { title: null, status: 'story_review' })]);
    const { root } = await setup(repository);
    const action = root.querySelector<HTMLButtonElement>('.next-action-row__action');
    expect(action?.getAttribute('aria-label')).toContain(episodesVi.list.untitled);
    expect(root.querySelector('.next-action-row__title')?.textContent).toContain(episodesVi.list.untitled);
  });

  it('RetryButton_AfterFailure_RecoversAndShowsRows', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.failStatus('ready', {
      code: 'internal_error',
      status: 500,
      kind: 'server',
      fieldErrors: {},
      traceId: 't-1',
    });
    const { harness, root } = await setup(repository);
    expect(root.querySelector('nts-error-state')).not.toBeNull();
    // Re-can everything (the fake clears failures on withStatus), then click the ErrorState retry.
    for (const status of DASHBOARD_STATUSES) repository.withStatus(status, []);
    repository.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    root.querySelector<HTMLButtonElement>('nts-error-state button')?.click();
    await flush(harness.fixture);
    expect(root.querySelector('nts-error-state')).toBeNull();
    expect(root.querySelectorAll('li.next-action-row')).toHaveLength(1);
    expect(repository.calls).toHaveLength(DASHBOARD_STATUSES.length * 2);
  });
});
