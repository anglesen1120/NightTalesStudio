import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { EpisodeFiltersComponent } from './episode-filters.component';
import type { EpisodeFiltersViewModel } from './episode-list.view-model';

// QA (10.5.1-T1): the series option titles and a deep-linked series id both reach the filter DOM. Prove they render
// as text (never as elements), which is the escaping half of the "unknown deep-linked id is shown safely" contract.

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

const SERIES_ID = '11111111-1111-4111-8111-111111111111';

function model(overrides: Partial<EpisodeFiltersViewModel> = {}): EpisodeFiltersViewModel {
  return {
    status: null,
    seriesId: null,
    seriesOptions: [],
    seriesOptionsState: 'ready',
    seriesOptionsBounded: false,
    disabled: false,
    ...overrides,
  };
}

async function setup(filters: EpisodeFiltersViewModel) {
  await TestBed.configureTestingModule({
    imports: [EpisodeFiltersComponent, translations],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(EpisodeFiltersComponent);
  fixture.componentRef.setInput('filters', filters);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeFiltersComponent (QA)', () => {
  it('renders a series option title containing markup as inert text', async () => {
    const payload = '<img src=x onerror="window.__ntsQaXss=1">Khu trọ';
    const { root } = await setup(model({ seriesOptions: [{ id: SERIES_ID, title: payload }] }));
    const [, seriesSelect] = root.querySelectorAll<HTMLSelectElement>('select');

    expect(seriesSelect?.querySelector('option img')).toBeNull();
    const option = seriesSelect?.querySelector<HTMLOptionElement>(`option[value="${SERIES_ID}"]`);
    expect(option?.textContent).toContain(payload);
  });

  it('renders an unresolved deep-linked id containing markup as inert text', async () => {
    const payload = '<img src=x onerror="window.__ntsQaXss=1">';
    const { root } = await setup(model({ seriesId: payload, seriesOptions: [] }));
    const [, seriesSelect] = root.querySelectorAll<HTMLSelectElement>('select');

    expect(seriesSelect?.querySelector('option img')).toBeNull();
    expect(root.textContent).toContain(payload);
  });

  it('preserves interpolation-shaped user titles as literal text in the chip and its accessible name', async () => {
    const title = '{{ name }} {{x}} <strong>literal</strong>';
    const { root } = await setup(model({ seriesId: SERIES_ID, seriesOptions: [{ id: SERIES_ID, title }] }));
    const chip = root.querySelector('.episode-filters__chip');
    expect(chip?.textContent).toContain(title);
    expect(chip?.tagName).toBe('BUTTON');
    expect(chip?.getAttribute('aria-label')).toBe(`${episodesVi.list.clearSeriesFilter}: ${title}`);
    expect(chip?.querySelector('strong')).toBeNull();
  });
});
