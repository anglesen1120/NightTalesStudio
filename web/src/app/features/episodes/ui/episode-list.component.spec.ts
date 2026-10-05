import { ChangeDetectionStrategy, Component } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import type { AppError } from '@core/errors';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import type { EpisodeListItem } from '../domain/episode.model';
import { episodeItem } from '../testing/fake-episodes.repository';
import { EpisodeListComponent } from './episode-list.component';

const translations = TranslocoTestingModule.forRoot({
  langs: { 'episodes/vi': episodesVi, 'errors/vi': errorsVi },
  translocoConfig: {
    availableLangs: ['vi'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class EpisodeDetailStub {}

const networkError: AppError = { code: 'network_error', status: 0, kind: 'network', fieldErrors: {} };


interface Inputs {
  items?: EpisodeListItem[];
  loading?: boolean;
  loadingMore?: boolean;
  hasMore?: boolean;
  error?: AppError | null;
  isEmpty?: boolean;
  filtered?: boolean;
}

async function setup(inputs: Inputs = {}) {
  await TestBed.configureTestingModule({
    imports: [EpisodeListComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'episodes/:id', component: EpisodeDetailStub }]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(EpisodeListComponent);
  apply(fixture, inputs);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

function apply(fixture: ComponentFixture<EpisodeListComponent>, inputs: Inputs): void {
  fixture.componentRef.setInput('items', inputs.items ?? []);
  fixture.componentRef.setInput('loading', inputs.loading ?? false);
  fixture.componentRef.setInput('loadingMore', inputs.loadingMore ?? false);
  fixture.componentRef.setInput('hasMore', inputs.hasMore ?? false);
  fixture.componentRef.setInput('error', inputs.error ?? null);
  fixture.componentRef.setInput('isEmpty', inputs.isEmpty ?? false);
  fixture.componentRef.setInput('filtered', inputs.filtered ?? false);
}

function loadMoreButton(root: HTMLElement): HTMLButtonElement | null {
  return root.querySelector('.episode-list__load-more');
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeListComponent', () => {
  it('renders a skeleton on the first load, before any items', async () => {
    const { root } = await setup({ loading: true });
    expect(root.querySelector('nts-loading-state')).not.toBeNull();
    expect(root.querySelector('ul')).toBeNull();
    expect(root.textContent).toContain('Đang tải danh sách tập phim');
  });

  it('renders rows in list order', async () => {
    const items = [episodeItem(3), episodeItem(1), episodeItem(2)];
    const { root } = await setup({ items });
    const titles = [...root.querySelectorAll('.episode-row__title')].map((a) => a.textContent?.trim());
    expect(titles).toEqual(['Episode 3', 'Episode 1', 'Episode 2']);
    expect(root.querySelectorAll('li > nts-episode-row')).toHaveLength(3);
  });

  it('emits loadMore only while hasMore and not loadingMore', async () => {
    const { fixture, root, component } = await setup({ items: [episodeItem(1)], hasMore: true });
    const emitted: number[] = [];
    onEmit(component.loadMore, () => emitted.push(1));

    loadMoreButton(root)?.click();
    expect(emitted).toHaveLength(1);

    // In flight: the button stays focusable (aria-disabled, not disabled) and ignores a second press.
    apply(fixture, { items: [episodeItem(1)], hasMore: true, loadingMore: true });
    fixture.detectChanges();
    await fixture.whenStable();
    const busy = loadMoreButton(root);
    expect(busy?.disabled).toBe(false);
    expect(busy?.getAttribute('aria-disabled')).toBe('true');
    busy?.click();
    expect(emitted).toHaveLength(1);
  });

  it('hides the button once the last page arrived', async () => {
    const { root } = await setup({ items: [episodeItem(1)], hasMore: false });
    expect(loadMoreButton(root)).toBeNull();
  });

  it('keeps the button on an empty page that still carries a cursor', async () => {
    const { root } = await setup({ items: [], isEmpty: false, hasMore: true });
    expect(loadMoreButton(root)).not.toBeNull();
    expect(root.querySelector('nts-empty-state')).toBeNull();
  });

  it('toggles aria-busy on the list and the button while a page loads', async () => {
    const { fixture, root } = await setup({ items: [episodeItem(1)], hasMore: true, loadingMore: true });
    expect(root.querySelector('ul')?.getAttribute('aria-busy')).toBe('true');
    expect(loadMoreButton(root)?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelectorAll('li[aria-hidden="true"]')).toHaveLength(2);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Đang tải thêm tập phim');

    apply(fixture, { items: [episodeItem(1), episodeItem(2)], hasMore: false });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('ul')?.getAttribute('aria-busy')).toBe('false');
  });

  it('announces appended rows in the polite live region', async () => {
    const { fixture, root } = await setup({ items: [episodeItem(1), episodeItem(2)], hasMore: true });
    apply(fixture, {
      items: [episodeItem(1), episodeItem(2), episodeItem(3), episodeItem(4)],
      hasMore: true,
    });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Đã tải thêm 2 tập phim');
  });

  it('renders EmptyState with the series action when nothing is loaded and no cursor remains', async () => {
    const { root, component } = await setup({ isEmpty: true });
    const empty = root.querySelector('nts-empty-state');
    expect(empty).not.toBeNull();
    expect(root.textContent).toContain('Chưa có tập phim nào');
    let actions = 0;
    onEmit(component.emptyAction, () => actions++);
    empty?.querySelector<HTMLButtonElement>('button')?.click();
    expect(actions).toBe(1);
  });

  it('renders the filtered-empty variant with a clear-filters action', async () => {
    const { root, component } = await setup({ isEmpty: true, filtered: true });
    expect(root.textContent).toContain('Không có tập phù hợp');
    let clears = 0;
    onEmit(component.clearFilters, () => clears++);
    root.querySelector<HTMLButtonElement>('nts-empty-state button')?.click();
    expect(clears).toBe(1);
  });

  it('renders ErrorState with the errors.<code> copy, a trace id and retry', async () => {
    const { root, component } = await setup({
      items: [episodeItem(1)],
      error: { ...networkError, traceId: 'a3f19c' },
    });
    const state = root.querySelector('nts-error-state');
    expect(state).not.toBeNull();
    expect(root.textContent).toContain('Không tải được tập phim');
    expect(root.textContent).toContain(errorsVi.network_error);
    expect(root.textContent).toContain('a3f19c');
    // The wire's raw detail is never rendered (D9) — the copy comes from errors.<code>.
    expect(root.textContent).not.toContain('PROBLEM_DETAIL');

    let retries = 0;
    onEmit(component.retry, () => retries++);
    state?.querySelector<HTMLButtonElement>('button')?.click();
    expect(retries).toBe(1);
  });

  it('keeps Tab order: row links come before the load-more footer button', async () => {
    const { root } = await setup({ items: [episodeItem(1), episodeItem(2)], hasMore: true });
    const tabbables = [...root.querySelectorAll<HTMLElement>('a, button')];
    const loadMoreIndex = tabbables.findIndex((el) => el.classList.contains('episode-list__load-more'));
    let lastRowLink = -1;
    for (let i = tabbables.length - 1; i >= 0; i--) {
      const el = tabbables[i];
      if (el?.tagName === 'A') {
        lastRowLink = i;
        break;
      }
    }
    expect(loadMoreIndex).toBeGreaterThan(-1);
    expect(lastRowLink).toBeLessThan(loadMoreIndex);
  });
});
