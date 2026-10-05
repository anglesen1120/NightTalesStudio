import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ChangeDetectionStrategy, Component } from '@angular/core';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import dashboardVi from '../../../../assets/i18n/dashboard/vi.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import type { EpisodeStatus } from '@features/episodes';
import type { NextActionRowViewModel } from '../dashboard.model';
import { dashboardEpisode } from '../testing/fake-episode.repository';
import { NextActionRow } from './next-action-row';

// QA (10.4.1-T1): adversarial row paths the shipped spec does not cover — whitespace titles falling back to the
// untitled label, an invalid `updatedAt` string rendering the "updated just now" fallback instead of "NaN", an
// unknown wire status rendering the unknown icon rather than crashing, and the primary-action output carrying
// the whole view model (the page needs `episode.id` and `primaryAction` to build the destination).

const translations = TranslocoTestingModule.forRoot({
  langs: {
    'dashboard/vi': dashboardVi,
    'episodes/vi': episodesVi,
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

function viewModel(overrides: Partial<NextActionRowViewModel> = {}): NextActionRowViewModel {
  return {
    episode: dashboardEpisode(1, { title: 'Chuyện dưới trăng', status: 'story_review', updatedAt: '2026-10-05T09:00:00Z' }),
    group: 'needsReview',
    primaryAction: 'reviewStory',
    ...overrides,
  };
}

async function setup(vm: NextActionRowViewModel = viewModel()) {
  await TestBed.configureTestingModule({
    imports: [NextActionRow, translations],
    providers: [...ZONELESS_TEST_PROVIDERS, provideRouter([{ path: 'episodes/:id', component: StubPage }])],
  }).compileComponents();
  const fixture = TestBed.createComponent(NextActionRow);
  fixture.componentRef.setInput('vm', vm);
  fixture.componentRef.setInput('now', new Date('2026-10-05T10:00:00Z').getTime());
  fixture.componentRef.setInput('untitledLabel', episodesVi.list.untitled);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}

afterEach(() => TestBed.resetTestingModule());

describe('NextActionRow (QA)', () => {
  it('WhitespaceTitle_FallsBackToUntitled', async () => {
    const { fixture, root } = await setup();
    const vm = viewModel();
    fixture.componentRef.setInput('vm', {
      ...vm,
      episode: { ...vm.episode, title: '   ' },
    });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.next-action-row__title')?.textContent).toContain(episodesVi.list.untitled);
    expect(root.querySelector('.next-action-row__action')?.getAttribute('aria-label')).toContain(
      episodesVi.list.untitled,
    );
  });

  it('EmptyTitle_FallsBackToUntitled', async () => {
    const vm = viewModel();
    const { root } = await setup({ ...vm, episode: { ...vm.episode, title: '' } });
    expect(root.querySelector('.next-action-row__title')?.textContent).toContain(episodesVi.list.untitled);
    expect(root.querySelector('.next-action-row__action')?.getAttribute('aria-label')).toContain(
      episodesVi.list.untitled,
    );
  });

  it('InvalidUpdatedAt_RendersJustNow_FallbackNotNaN', async () => {
    const vm = viewModel();
    const { root } = await setup({ ...vm, episode: { ...vm.episode, updatedAt: 'not-a-date' } });
    const time = root.querySelector('time');
    expect(time?.textContent).toContain(dashboardVi.updatedJustNow);
    expect(time?.textContent).not.toContain('NaN');
    // The absolute tooltip falls back to the raw value instead of an invalid-date string.
    expect(time?.getAttribute('title')).toBe('not-a-date');
  });

  it('UnknownWireStatus_RendersUnknownIcon_NoCrash', async () => {
    const vm = viewModel();
    const { root } = await setup({
      ...vm,
      episode: { ...vm.episode, status: 'mystery_status' as EpisodeStatus },
    });
    expect(root.querySelector('nts-icon-unknown')).not.toBeNull();
    // The chip text is the raw scope key (no translation exists); it is never a blank or a crash.
    const chip = root.querySelector('.next-action-row__status');
    expect(chip?.textContent).toContain('episodes.status.mystery_status');
  });

  it('PrimaryAction_EmitsTheWholeViewModel', async () => {
    const { root, component } = await setup();
    const emitted: NextActionRowViewModel[] = [];
    onEmit(component.primaryAction, (vm) => emitted.push(vm));
    root.querySelector<HTMLButtonElement>('.next-action-row__action')?.click();
    expect(emitted).toHaveLength(1);
    expect(emitted[0]?.episode.id).toBe('dep-1');
    expect(emitted[0]?.primaryAction).toBe('reviewStory');
  });

  it('BothControls_AreKeyboardFocusableButtons', async () => {
    const { root } = await setup();
    const title = root.querySelector<HTMLButtonElement>('.next-action-row__title');
    const action = root.querySelector<HTMLButtonElement>('.next-action-row__action');
    expect(title?.tagName).toBe('BUTTON');
    expect(action?.tagName).toBe('BUTTON');
    expect(title?.disabled).toBe(false);
    expect(action?.disabled).toBe(false);
    title?.focus();
    expect(document.activeElement).toBe(title);
    action?.focus();
    expect(document.activeElement).toBe(action);
  });
});
