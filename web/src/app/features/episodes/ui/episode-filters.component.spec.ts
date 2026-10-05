import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { LIST_STATUSES } from '../domain/episode.model';
import { EpisodeFiltersComponent } from './episode-filters.component';
import type { EpisodeFiltersViewModel } from './episode-list.view-model';

const translations = TranslocoTestingModule.forRoot({
  langs: { 'episodes/vi': episodesVi, 'episodes/en': episodesEn },
  translocoConfig: {
    availableLangs: ['vi', 'en'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

const SERIES_A = '11111111-1111-4111-8111-111111111111';
const SERIES_B = '22222222-2222-4222-8222-222222222222';

function model(overrides: Partial<EpisodeFiltersViewModel> = {}): EpisodeFiltersViewModel {
  return {
    status: null,
    seriesId: null,
    seriesOptions: [
      { id: SERIES_A, title: 'Chuyện ma quanh khu trọ' },
      { id: SERIES_B, title: 'Vụ án Cát Lái' },
    ],
    seriesOptionsState: 'ready',
    seriesOptionsBounded: false,
    disabled: false,
    ...overrides,
  };
}

async function setup(filters: EpisodeFiltersViewModel = model()) {
  await TestBed.configureTestingModule({
    imports: [EpisodeFiltersComponent, translations],
    providers: [...ZONELESS_TEST_PROVIDERS],
  }).compileComponents();
  const fixture = TestBed.createComponent(EpisodeFiltersComponent);
  fixture.componentRef.setInput('filters', filters);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

/** The template always renders the status select before the series select; fail early if that changes. */
function selects(root: HTMLElement): [status: HTMLSelectElement, series: HTMLSelectElement] {
  const [status, series] = root.querySelectorAll<HTMLSelectElement>('select');
  if (!status || !series) throw new Error('expected the status and series selects to render');
  return [status, series];
}

function change(select: HTMLSelectElement, value: string): void {
  select.value = value;
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeFiltersComponent', () => {
  it('binds every label to its control via for/id', async () => {
    const { root } = await setup();
    for (const label of [...root.querySelectorAll<HTMLLabelElement>('label')]) {
      const control = root.querySelector<HTMLElement>(`#${label.htmlFor}`);
      expect(control, `label "${label.textContent}" points at a control`).not.toBeNull();
      expect(control?.tagName).toBe('SELECT');
    }
  });

  it('offers every LIST_STATUSES literal plus "all" and emits the literal', async () => {
    const { root, component } = await setup();
    const [statusSelect] = selects(root);
    const options = [...statusSelect.querySelectorAll('option')];
    expect(options).toHaveLength(LIST_STATUSES.length + 1);
    expect(options[0]?.value).toBe('');
    for (const status of LIST_STATUSES) {
      expect(options.some((option) => option.value === status)).toBe(true);
    }
    // The cancelled status is never offered (the list endpoint excludes it).
    expect(options.some((option) => option.value === 'cancelled')).toBe(false);

    const emitted: (string | null)[] = [];
    onEmit(component.statusChange, (value) => emitted.push(value));
    change(statusSelect, 'story_review');
    expect(emitted).toEqual(['story_review']);
    change(statusSelect, '');
    expect(emitted).toEqual(['story_review', null]);
  });

  it('guards the emitted status with the closed list (unknown values emit null)', async () => {
    const { root, component } = await setup();
    const [statusSelect] = selects(root);
    const emitted: (string | null)[] = [];
    onEmit(component.statusChange, (value) => emitted.push(value));
    const rogue = document.createElement('option');
    rogue.value = 'constructor';
    statusSelect.append(rogue);
    change(statusSelect, 'constructor');
    expect(emitted).toEqual([null]);
  });

  it('renders the story_review option with the plan §6 copy the e2e asserts', async () => {
    const { root } = await setup();
    const [statusSelect] = selects(root);
    const option = [...statusSelect.querySelectorAll('option')].find((o) => o.value === 'story_review');
    expect(option?.textContent?.trim()).toBe('Chờ bạn duyệt');
  });

  it('renders series options by title and emits the id', async () => {
    const { root, component } = await setup();
    const [, seriesSelect] = selects(root);
    const titles = [...seriesSelect.querySelectorAll('option')].map((o) => o.textContent?.trim());
    expect(titles).toContain('Chuyện ma quanh khu trọ');
    expect(titles).toContain('Vụ án Cát Lái');

    const emitted: (string | null)[] = [];
    onEmit(component.seriesChange, (value) => emitted.push(value));
    change(seriesSelect, SERIES_A);
    expect(emitted).toEqual([SERIES_A]);
    change(seriesSelect, '');
    expect(emitted).toEqual([SERIES_A, null]);
  });

  it('emits null from each active-filter chip and void from clear-all', async () => {
    const { root, component } = await setup(model({ status: 'story_review', seriesId: SERIES_A }));
    const statusEmitted: (string | null)[] = [];
    const seriesEmitted: (string | null)[] = [];
    let cleared = 0;
    onEmit(component.statusChange, (value) => statusEmitted.push(value));
    onEmit(component.seriesChange, (value) => seriesEmitted.push(value));
    onEmit(component.clearFilters, () => cleared++);

    const buttons = [...root.querySelectorAll<HTMLButtonElement>('.episode-filters__chip')];
    expect(buttons).toHaveLength(2);
    buttons[0]?.click();
    buttons[1]?.click();
    expect(statusEmitted).toEqual([null]);
    expect(seriesEmitted).toEqual([null]);

    root.querySelector<HTMLButtonElement>('.episode-filters__clear-all')?.click();
    expect(cleared).toBe(1);
  });

  it('names the series chip with the resolved title', async () => {
    const { root } = await setup(model({ seriesId: SERIES_B }));
    const chip = [...root.querySelectorAll('.episode-filters__chip')].at(-1);
    expect(chip?.textContent).toContain('Vụ án Cát Lái');
    expect(chip?.tagName).toBe('BUTTON');
    expect(chip?.getAttribute('aria-label')).toBe(`${episodesVi.list.clearSeriesFilter}: Vụ án Cát Lái`);
  });

  it('keeps Transloco-shaped user titles literal in the series-chip accessible name in both languages', async () => {
    const title = '{{ name }} {{x}}';
    const { fixture, root } = await setup(model({ seriesId: SERIES_A, seriesOptions: [{ id: SERIES_A, title }] }));
    const button = root.querySelector('button.episode-filters__chip');
    expect(button?.getAttribute('aria-label')).toBe(`${episodesVi.list.clearSeriesFilter}: ${title}`);
    TestBed.inject(TranslocoService).setActiveLang('en');
    await fixture.whenStable();
    expect(button?.getAttribute('aria-label')).toBe(`${episodesEn.list.clearSeriesFilter}: ${title}`);
  });

  it('disables the series select with the unavailable note and keeps an unresolved id applied', async () => {
    const { root } = await setup(
      model({ seriesId: SERIES_A, seriesOptions: [], seriesOptionsState: 'unavailable' }),
    );
    const [, seriesSelect] = selects(root);
    expect(seriesSelect.disabled).toBe(true);
    expect(root.textContent).toContain(episodesVi.list.seriesFilterUnavailable);
    // The deep-linked id stays selected and visible as text (the only option while options are gone).
    expect(seriesSelect.value).toBe(SERIES_A);
    const option = seriesSelect.querySelector('option[value]');
    expect(option?.textContent?.trim()).toBe(SERIES_A);
  });

  it('shows the bounded note instead of claiming all series when the first page had a cursor', async () => {
    const { root } = await setup(model({ seriesOptionsBounded: true }));
    expect(root.textContent).toContain(episodesVi.list.seriesFilterBounded);
  });

  it('keeps the status select enabled while the series options are unavailable', async () => {
    const { root } = await setup(model({ seriesOptionsState: 'unavailable' }));
    const [statusSelect] = selects(root);
    expect(statusSelect.disabled).toBe(false);
  });

  it('disables both selects when the page marks filters disabled', async () => {
    const { root } = await setup(model({ disabled: true }));
    for (const select of selects(root)) expect(select.disabled).toBe(true);
  });
});
