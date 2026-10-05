import { signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { SessionStore } from '@core/session';
import { EpisodesRepository } from '@features/episodes';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import dashboardVi from '../../../../assets/i18n/dashboard/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { FakeDashboardEpisodesRepository, dashboardEpisode } from '../testing/fake-episode.repository';
import { DashboardPage } from './dashboard-page';

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
      provideRouter([{ path: 'dashboard', component: DashboardPage }]),
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
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
  vi.useRealTimers();
  if (originalVisibilityDescriptor === undefined) Reflect.deleteProperty(document, 'visibilityState');
  else Object.defineProperty(document, 'visibilityState', originalVisibilityDescriptor);
});

describe('DashboardPage', () => {
  it('ShowsSkeletons_WhileLoading', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.defer();
    const { root } = await setup(repository);
    expect(root.querySelectorAll('section[aria-busy="true"]')).toHaveLength(3);
    expect(root.querySelectorAll('nts-loading-state')).toHaveLength(3);
    repository.resolveAll();
  });

  it('ShowsEmptyStates_PerGroup', async () => {
    const { root } = await setup();
    expect(root.querySelectorAll('nts-empty-state')).toHaveLength(3);
    expect(root.querySelector<HTMLAnchorElement>('.dashboard-page__all-empty a')?.getAttribute('href')).toBe(
      '/series/new',
    );
  });

  it('ShowsErrorState_OnFailure', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.failStatus('ready', {
      code: 'unknown_error',
      status: 500,
      kind: 'unknown',
      fieldErrors: {},
      traceId: 'trace-dashboard-1',
    });
    const { root } = await setup(repository);
    expect(root.querySelector('nts-error-state')).not.toBeNull();
    expect(root.querySelector('nts-error-state button')).not.toBeNull();
    expect(root.querySelector('nts-error-state')?.textContent).toContain('trace-dashboard-1');
    expect(root.querySelector('.dashboard-group__rows')).toBeNull();
  });

  it('RateLimitedZero_ShowsEnabledCountdownRetry', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.failStatus('ready', {
      code: 'rate_limited',
      status: 429,
      kind: 'rate_limit',
      fieldErrors: {},
      retryAfterSec: 0,
    });
    const { root } = await setup(repository);
    expect(root.querySelector('nts-retry-countdown')).not.toBeNull();
    expect(root.querySelector<HTMLButtonElement>('.dashboard-page__retry button')?.disabled).toBe(false);
  });

  it('PartialRefreshFailure_KeepsRows', async () => {
    const repository = new FakeDashboardEpisodesRepository();
    repository.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    const { harness, root } = await setup(repository);
    repository.failStatus('ready', {
      code: 'network_error',
      status: 0,
      kind: 'network',
      fieldErrors: {},
    });
    root.querySelector<HTMLButtonElement>('.dashboard-page__refresh')?.click();
    await flush(harness.fixture);
    expect(root.querySelectorAll('li.next-action-row')).toHaveLength(1);
    expect(root.querySelector('.dashboard-page__refresh-error')?.textContent).toContain(
      dashboardVi.partialRefreshFailed,
    );
  });

  it('AnnouncesCounts_OncePerRefresh', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const repository = new FakeDashboardEpisodesRepository();
    repository.withStatus('story_review', [dashboardEpisode(1, { status: 'story_review' })]);
    const { harness, root } = await setup(repository);
    const liveRegions = root.querySelectorAll('[aria-live="polite"].dashboard-page__announcement');
    expect(liveRegions).toHaveLength(1);
    expect(liveRegions[0]?.textContent).toContain('1');

    vi.setSystemTime(new Date('2026-10-05T10:00:11Z'));
    root.querySelector<HTMLButtonElement>('.dashboard-page__refresh')?.click();
    await flush(harness.fixture);
    expect(root.querySelectorAll('[aria-live="polite"].dashboard-page__announcement')).toHaveLength(1);
    expect(repository.calls).toHaveLength(10);
  });

  it('HiddenDocument_IssuesNoRequest', async () => {
    const { repository } = await setup(new FakeDashboardEpisodesRepository(), 'hidden');
    expect(repository.calls).toHaveLength(0);
  });

  it('RefocusThrottle_AtMostOncePer10s', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-05T10:00:00Z'));
    const { harness, repository } = await setup();
    expect(repository.calls).toHaveLength(5);

    vi.setSystemTime(new Date('2026-10-05T10:00:11Z'));
    window.dispatchEvent(new Event('focus'));
    await flush(harness.fixture);
    expect(repository.calls).toHaveLength(10);

    vi.setSystemTime(new Date('2026-10-05T10:00:12Z'));
    window.dispatchEvent(new Event('focus'));
    await flush(harness.fixture);
    expect(repository.calls).toHaveLength(10);

    vi.setSystemTime(new Date('2026-10-05T10:00:22Z'));
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(harness.fixture);
    expect(repository.calls).toHaveLength(15);
  });
});
