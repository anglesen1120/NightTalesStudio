import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodesRepository, episodeItem } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { EpisodesListPage } from './episodes-list.page';

// QA (10.5.1-T1): the acceptance delta wants the query-param contract and the route/param-only/session lifecycle
// proved adversarially — invalid and repeated params, a cursor never read from the URL, and a superseded page that
// must not survive a param-only route reuse or a visit to a public route without logout.

const translations = TranslocoTestingModule.forRoot({
  langs: { 'episodes/vi': episodesVi },
  translocoConfig: {
    availableLangs: ['vi'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({ template: 'public-route', changeDetection: ChangeDetectionStrategy.OnPush })
class PublicStubPage {}

const sessionStub = { generation: signal(0) };

const SERIES_ID = '11111111-1111-4111-8111-111111111111';

async function flush(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) await Promise.resolve();
  await fixture.whenStable();
}

interface Setup {
  harness: RouterTestingHarness;
  episodes: FakeEpisodesRepository;
  series: FakeSeriesOptionsRepository;
  root: HTMLElement;
}

async function setup(
  url = '/episodes',
  episodes = new FakeEpisodesRepository(),
  series = new FakeSeriesOptionsRepository(),
): Promise<Setup> {
  await TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'episodes', component: EpisodesListPage },
        { path: 'series', component: PublicStubPage },
        { path: 'login', component: PublicStubPage },
      ]),
      { provide: EpisodesRepository, useValue: episodes },
      { provide: SeriesOptionsRepository, useValue: series },
      { provide: SessionStore, useValue: sessionStub },
    ],
  }).compileComponents();
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, EpisodesListPage);
  await flush(harness.fixture);
  return { harness, episodes, series, root: harness.routeNativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodesListPage (QA)', () => {
  it('ignores ?status=cancelled, a value the list never offers and the API never returns', async () => {
    const { root, episodes } = await setup('/episodes?status=cancelled');
    expect(episodes.calls).toEqual([{ limit: 20 }]);
    expect(root.querySelector<HTMLSelectElement>('select')?.value).toBe('');
  });

  it('ignores ?status with the wrong case instead of sending it to the API', async () => {
    const { episodes } = await setup('/episodes?status=Story_Review');
    expect(episodes.calls).toEqual([{ limit: 20 }]);
  });

  it('takes the first value of a repeated ?status and sends exactly one scalar', async () => {
    const { root, episodes } = await setup('/episodes?status=ready&status=draft');
    expect(episodes.calls).toEqual([{ limit: 20, status: 'ready' }]);
    expect(root.querySelector<HTMLSelectElement>('select')?.value).toBe('ready');
  });

  it('accepts an upper-case ?series uuid and keeps it as the single scalar filter', async () => {
    const upper = SERIES_ID.toUpperCase();
    const { episodes } = await setup(`/episodes?series=${upper}`);
    expect(episodes.calls).toEqual([{ limit: 20, seriesId: upper }]);
  });

  it('never reads a cursor from the URL', async () => {
    const { episodes } = await setup('/episodes?cursor=opaque-injection');
    expect(episodes.calls).toEqual([{ limit: 20 }]);
  });

  it('unfiltered empty list shows the empty state and its action opens /series', async () => {
    const episodes = new FakeEpisodesRepository().willReturn({ items: [], nextCursor: null });
    const { harness, root } = await setup('/episodes', episodes);
    expect(root.querySelector('nts-empty-state')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('nts-empty-state button')?.click();
    await flush(harness.fixture);
    expect(TestBed.inject(Router).url).toBe('/series');
  });

  it('two rapid load-more presses issue exactly one page request', async () => {
    const episodes = new FakeEpisodesRepository()
      .willReturn({ items: [episodeItem(1)], nextCursor: 'c1' })
      .willReturn({ items: [episodeItem(2)], nextCursor: null });
    const { harness, root } = await setup('/episodes', episodes);
    const button = root.querySelector<HTMLButtonElement>('.episode-list__load-more');

    button?.click();
    button?.click();
    await flush(harness.fixture);

    expect(episodes.calls.filter((call) => call.cursor === 'c1')).toHaveLength(1);
    expect(episodes.calls).toHaveLength(2);
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(2);
  });

  it('a superseded response of a param-only route reuse never replaces the newer list', async () => {
    const episodes = new FakeEpisodesRepository().defer();
    const { harness, episodes: repo, series } = await setup('/episodes?status=failed', episodes);

    await harness.navigateByUrl('/episodes?status=ready', EpisodesListPage);
    await flush(harness.fixture);

    // The store is not re-created on a param-only change (one options load for the whole visit).
    expect(series.calls).toBe(1);
    expect(repo.calls).toEqual([
      { limit: 20, status: 'failed' },
      { limit: 20, status: 'ready' },
    ]);

    repo.resolveNext({ items: [episodeItem(9)], nextCursor: null }, 0); // the superseded 'failed' page
    repo.resolveNext({ items: [episodeItem(5)], nextCursor: null }, 0);
    await flush(harness.fixture);

    const root = harness.routeNativeElement as HTMLElement;
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(1);
    expect(root.textContent).toContain('Episode 5');
    expect(root.textContent).not.toContain('Episode 9');
  });

  it('fills in the series title when the options arrive after a deep-linked id', async () => {
    const episodes = new FakeEpisodesRepository().willReturn({ items: [], nextCursor: null });
    const series = new FakeSeriesOptionsRepository().defer();
    const { harness, root } = await setup(`/episodes?series=${SERIES_ID}`, episodes, series);

    expect(root.textContent).toContain(SERIES_ID);
    series.resolve({ items: [{ id: SERIES_ID, title: 'Alpha' }], bounded: false });
    await flush(harness.fixture);

    const [, seriesSelect] = root.querySelectorAll<HTMLSelectElement>('select');
    expect(seriesSelect?.selectedOptions[0]?.textContent).toContain('Alpha');
    expect(root.textContent).not.toContain(SERIES_ID);
  });

  it('clearing one filter chip keeps the other filter applied in both request and URL', async () => {
    const episodes = new FakeEpisodesRepository()
      .willReturn({ items: [], nextCursor: null })
      .willReturn({ items: [], nextCursor: null });
    const series = new FakeSeriesOptionsRepository();
    series.page = { items: [{ id: SERIES_ID, title: 'Alpha' }], bounded: false };
    const { harness, root, episodes: repo } = await setup(
      `/episodes?status=failed&series=${SERIES_ID}`,
      episodes,
      series,
    );

    const statusChip = root.querySelector<HTMLButtonElement>('.episode-filters__chip');
    statusChip?.click();
    await flush(harness.fixture);

    expect(repo.calls.at(-1)).toEqual({ limit: 20, seriesId: SERIES_ID });
    expect(TestBed.inject(Router).url).toBe(`/episodes?series=${SERIES_ID}`);
  });

  it('a late response after leaving to a public route never reaches a later visit', async () => {
    const episodes = new FakeEpisodesRepository().defer();
    const { harness, episodes: repo, series, root: oldRoot } = await setup('/episodes', episodes);

    await harness.navigateByUrl('/login');
    repo.resolveNext({ items: [episodeItem(9)], nextCursor: 'c9' });
    await flush(harness.fixture);
    expect(oldRoot.querySelectorAll('nts-episode-row')).toHaveLength(0);

    await harness.navigateByUrl('/episodes', EpisodesListPage);
    await flush(harness.fixture);
    repo.resolveNext({ items: [episodeItem(5)], nextCursor: null });
    await flush(harness.fixture);

    // A new visit gets a new page-scoped store: two options loads, and only the fresh page's rows.
    expect(series.calls).toBe(2);
    const root = harness.routeNativeElement as HTMLElement;
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(1);
    expect(root.textContent).toContain('Episode 5');
    expect(root.textContent).not.toContain('Episode 9');
  });
});
