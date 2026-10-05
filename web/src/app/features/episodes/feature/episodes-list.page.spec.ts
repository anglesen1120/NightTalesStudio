import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest';
import { SessionStore } from '@core/session';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { EpisodeListStore } from '../application/episode-list.store';
import { EpisodesRepository } from '../domain/episodes.repository';
import { SeriesOptionsRepository } from '../domain/series-options.repository';
import { FakeEpisodesRepository, episodeItem, type EpisodeListQuery } from '../testing/fake-episodes.repository';
import { FakeSeriesOptionsRepository } from '../testing/fake-series-options.repository';
import { EpisodesListPage } from './episodes-list.page';

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

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

/** The list store reads only `generation` (README §3.1 late-result gating); a stub stands in for the session. */
const sessionStub = { generation: signal(0) };

const SERIES_ID = '11111111-1111-4111-8111-111111111111';

/** Store setters coalesce on the microtask queue; the extra rounds cover deferred zoneless updates. */
async function flush(fixture: ComponentFixture<unknown>): Promise<void> {
  for (let i = 0; i < 3; i++) await Promise.resolve();
  await fixture.whenStable();
}

interface Setup {
  harness: RouterTestingHarness;
  episodes: FakeEpisodesRepository;
  series: FakeSeriesOptionsRepository;
  navigate: Mock<Router['navigate']>;
  root: HTMLElement;
}

async function setup(
  url = '/episodes',
  episodes = new FakeEpisodesRepository(),
  series = new FakeSeriesOptionsRepository(),
): Promise<Setup> {
  // A ready one-page option list by default; specs override `page`/`error` before calling setup.
  if (series.error === null && series.page.items.length === 0 && !series.page.bounded) {
    series.page = { items: [{ id: SERIES_ID, title: 'Chuyện ma quanh khu trọ' }], bounded: false };
  }

  await TestBed.configureTestingModule({
    imports: [translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([
        { path: 'episodes', component: EpisodesListPage },
        { path: 'episodes/:id', component: StubPage },
        { path: 'series', component: StubPage },
      ]),
      { provide: EpisodesRepository, useValue: episodes },
      { provide: SeriesOptionsRepository, useValue: series },
      { provide: SessionStore, useValue: sessionStub },
    ],
  }).compileComponents();
  const router = TestBed.inject(Router);
  const navigate = vi.spyOn(router, 'navigate');
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, EpisodesListPage);
  await flush(harness.fixture);
  return { harness, episodes, series, navigate, root: harness.routeNativeElement as HTMLElement };
}

afterEach(() => {
  TestBed.resetTestingModule();
  vi.restoreAllMocks();
});

describe('EpisodesListPage', () => {
  it('loads the first page and the series options once on open', async () => {
    const { episodes, series, root } = await setup('/episodes');
    expect(episodes.calls).toEqual([{ limit: 20 }]);
    expect(series.calls).toBe(1);
    expect(root.querySelector('h1')?.textContent?.trim()).toBe('Tập phim');
  });

  it('renders rows and a working Tải thêm that appends the next page; the cursor never reaches the URL', async () => {
    const episodes = new FakeEpisodesRepository()
      .willReturn({ items: [episodeItem(1), episodeItem(2)], nextCursor: 'c2' })
      .willReturn({ items: [episodeItem(3)], nextCursor: null });
    const { harness, root } = await setup('/episodes', episodes);

    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(2);
    const button = root.querySelector<HTMLButtonElement>('.episode-list__load-more');
    expect(button?.textContent?.trim()).toBe('Tải thêm');
    button?.click();
    await flush(harness.fixture);

    expect(episodes.calls).toEqual([{ limit: 20 }, { limit: 20, cursor: 'c2' }]);
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(3);
    expect(root.querySelector('.episode-list__load-more')).toBeNull();
    expect(TestBed.inject(Router).url).toBe('/episodes');
  });

  it('ignores an unknown ?status=/?series= — the request never carries it and the filter shows "all"', async () => {
    const { episodes, root } = await setup('/episodes?status=bogus&series=not-a-uuid');
    expect(episodes.calls).toEqual([{ limit: 20 }]);
    const [statusSelect, seriesSelect] = [...root.querySelectorAll<HTMLSelectElement>('select')];
    expect(statusSelect?.value).toBe('');
    expect(seriesSelect?.value).toBe('');
  });

  it('applies a valid ?status=/?series= deep link', async () => {
    const { episodes, root } = await setup(`/episodes?status=story_review&series=${SERIES_ID}`);
    expect(episodes.calls).toEqual([{ limit: 20, status: 'story_review', seriesId: SERIES_ID }]);
    const [statusSelect, seriesSelect] = [...root.querySelectorAll<HTMLSelectElement>('select')];
    expect(statusSelect?.value).toBe('story_review');
    expect(seriesSelect?.value).toBe(SERIES_ID);
  });

  it('writes exactly the validated filters to the URL with replaceUrl and no merge', async () => {
    const { harness, root, navigate, episodes } = await setup('/episodes');
    const statusSelect = root.querySelector<HTMLSelectElement>('select');
    statusSelect!.value = 'failed';
    statusSelect!.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(harness.fixture);

    expect(navigate).toHaveBeenCalledWith([], {
      queryParams: { status: 'failed', series: null },
      replaceUrl: true,
    });
    expect(episodes.calls.at(-1)).toEqual({ limit: 20, status: 'failed' });
  });

  it('drops a hand-crafted ?cursor= and junk params when a filter is written', async () => {
    const { harness, root, episodes } = await setup('/episodes?cursor=x&foo=1');
    const statusSelect = root.querySelector<HTMLSelectElement>('select');
    statusSelect!.value = 'failed';
    statusSelect!.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(harness.fixture);

    // Exactly the validated filter reaches the URL: the hand-crafted cursor and the junk param are gone (CR-3).
    expect(TestBed.inject(Router).url).toBe('/episodes?status=failed');
    expect(episodes.calls.every((call) => !('cursor' in call))).toBe(true);
  });

  it('keeps a filter intent whose store write lands before the navigation resolves', async () => {
    // A single-emission route (read before the page's effect exists) whose first page request never answers: the only
    // thing that can re-run the effect is a store write, which is exactly the CR-1 hazard. The store write is the half
    // of a filter intent that the UI performs before the router echo; the URL half is covered by the no-merge tests.
    const params = convertToParamMap({});
    const route = { snapshot: { queryParamMap: params }, queryParamMap: of(params) };
    const navigate = vi.fn<Router['navigate']>(() => new Promise<boolean>(() => undefined));
    const calls: EpisodeListQuery[] = [];
    let store: EpisodeListStore | null = null;
    const repo = {
      list(query: EpisodeListQuery) {
        calls.push(query);
        // First page still in flight: the user picks a status while the URL holds the old params.
        if (calls.length === 1) store?.setStatus('failed');
        return new Promise<never>(() => undefined);
      },
    };

    await TestBed.configureTestingModule({
      imports: [translations],
      providers: [
        ...ZONELESS_TEST_PROVIDERS,
        { provide: ActivatedRoute, useValue: route },
        { provide: Router, useValue: { navigate } },
        { provide: EpisodesRepository, useValue: repo },
        { provide: SeriesOptionsRepository, useValue: new FakeSeriesOptionsRepository() },
        { provide: SessionStore, useValue: sessionStub },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(EpisodesListPage);
    store = fixture.debugElement.injector.get(EpisodeListStore);
    fixture.detectChanges();
    await flush(fixture);
    TestBed.tick();
    await flush(fixture);

    // The store write must not re-apply the not-yet-echoed URL params: one intent, one request, filter kept (CR-1).
    expect(calls).toEqual([{ limit: 20 }, { limit: 20, status: 'failed' }]);
    expect(store.statusFilter()).toBe('failed');
    const statusSelect = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>('select');
    expect(statusSelect!.value).toBe('failed');
  });

  it('issues exactly one request for a param-only change on the reused component', async () => {
    const { harness, episodes } = await setup('/episodes');
    expect(episodes.calls).toHaveLength(1);

    await harness.navigateByUrl('/episodes?status=failed', EpisodesListPage);
    await flush(harness.fixture);

    expect(episodes.calls).toHaveLength(2);
    expect(episodes.calls[1]).toEqual({ limit: 20, status: 'failed' });
  });

  it('does not re-request when the URL echo matches the applied filters', async () => {
    const { harness, root, episodes } = await setup('/episodes');
    const statusSelect = root.querySelector<HTMLSelectElement>('select');
    statusSelect!.value = 'ready';
    statusSelect!.dispatchEvent(new Event('change', { bubbles: true }));
    await flush(harness.fixture);

    // One request for the intent; the page's own replaceUrl echo produced none.
    expect(episodes.calls).toEqual([{ limit: 20 }, { limit: 20, status: 'ready' }]);
  });

  it('clears both filters with one intent from the filtered empty state', async () => {
    const episodes = new FakeEpisodesRepository()
      .willReturn({ items: [], nextCursor: null })
      .willReturn({ items: [episodeItem(9)], nextCursor: null });
    const { harness, root } = await setup(`/episodes?status=failed&series=${SERIES_ID}`, episodes);

    expect(root.textContent).toContain('Không có tập phù hợp');
    root.querySelector<HTMLButtonElement>('nts-empty-state button')?.click();
    await flush(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/episodes');
    expect(episodes.calls.at(-1)).toEqual({ limit: 20 });
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(1);
  });

  it('keeps an unresolved deep-linked series id applied and shown as text', async () => {
    const unknownId = '99999999-9999-4999-8999-999999999999';
    const { episodes, root } = await setup(`/episodes?series=${unknownId}`);
    expect(episodes.calls).toEqual([{ limit: 20, seriesId: unknownId }]);
    const [, seriesSelect] = [...root.querySelectorAll<HTMLSelectElement>('select')];
    expect(seriesSelect?.value).toBe(unknownId);
    expect(root.textContent).toContain(unknownId);
  });

  it('disables only the series select when the options request fails; the list still works', async () => {
    const episodes = new FakeEpisodesRepository().willReturn({ items: [episodeItem(1)], nextCursor: null });
    const series = new FakeSeriesOptionsRepository();
    series.error = { code: 'network_error', status: 0, kind: 'network', fieldErrors: {} };
    const { root } = await setup('/episodes', episodes, series);

    const [statusSelect, seriesSelect] = [...root.querySelectorAll<HTMLSelectElement>('select')];
    expect(seriesSelect?.disabled).toBe(true);
    expect(statusSelect?.disabled).toBe(false);
    expect(root.textContent).toContain('Chưa tải được tên loạt bài');
    expect(root.querySelectorAll('nts-episode-row')).toHaveLength(1);
  });
});
