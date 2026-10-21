import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { LocaleService } from '@core/i18n';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import dashboardEn from '../../../../assets/i18n/dashboard/en.json';
import dashboardVi from '../../../../assets/i18n/dashboard/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import type { NextActionRowViewModel, PrimaryAction } from '../dashboard.model';
import { dashboardEpisode } from '../testing/fake-episode.repository';
import { NextActionRow } from './next-action-row';

const translations = TranslocoTestingModule.forRoot({
  langs: {
    'dashboard/vi': dashboardVi,
    'dashboard/en': dashboardEn,
    'episodes/vi': episodesVi,
    'episodes/en': episodesEn,
  },
  translocoConfig: {
    availableLangs: ['vi', 'en'],
    defaultLang: 'vi',
    fallbackLang: 'vi',
    reRenderOnLangChange: true,
  },
  preloadLangs: true,
});

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class StubPage {}

/** Semantic host: `TestBed.createComponent` hosts an attribute-selector component on a `<div>`, so the li contract is exercised inside a real template. */
@Component({
  imports: [NextActionRow],
  template:
    '<ul><li nts-next-action-row [vm]="vm()" [now]="now()" [untitledLabel]="untitledLabel()"></li></ul>',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class RowHost {
  readonly vm = input.required<NextActionRowViewModel>();
  readonly now = input.required<number>();
  readonly untitledLabel = input(episodesVi.list.untitled);
}

function viewModel(action: PrimaryAction = 'reviewStory'): NextActionRowViewModel {
  let status: NextActionRowViewModel['episode']['status'] = 'story_review';
  let group: NextActionRowViewModel['group'] = 'needsReview';
  if (action === 'reviewVideo') status = 'video_review';
  else if (action === 'open') {
    status = 'story_generating';
    group = 'inProgress';
  } else if (action === 'publish') {
    status = 'ready';
    group = 'ready';
  }
  return {
    episode: dashboardEpisode(1, { title: 'Chuyện dưới trăng', status, updatedAt: '2026-10-05T09:00:00Z' }),
    group,
    primaryAction: action,
  };
}

async function setup(vm: NextActionRowViewModel = viewModel()) {
  await TestBed.configureTestingModule({
    imports: [NextActionRow, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'episodes/:id', component: StubPage }]),
    ],
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

describe('NextActionRow', () => {
  it('PrimaryAction_PerGroup', async () => {
    const { fixture, root } = await setup();
    for (const action of ['reviewStory', 'reviewVideo', 'open', 'publish'] as const) {
      fixture.componentRef.setInput('vm', viewModel(action));
      fixture.detectChanges();
      await fixture.whenStable();
      expect(root.querySelector('.next-action-row__action')?.textContent?.trim()).toBe(
        dashboardVi.card.action[action],
      );
    }
  });

  it('AccessibleName_IncludesTitle', async () => {
    const { fixture, root } = await setup();
    expect(root.querySelector('.next-action-row__action')?.getAttribute('aria-label')).toContain(
      'Chuyện dưới trăng',
    );

    const untitled = viewModel();
    fixture.componentRef.setInput('vm', { ...untitled, episode: { ...untitled.episode, title: null } });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.next-action-row__action')?.getAttribute('aria-label')).toContain(
      episodesVi.list.untitled,
    );
  });

  it('StatusChip_HasIconAndText', async () => {
    const { root } = await setup();
    const chip = root.querySelector('.next-action-row__status');
    expect(chip?.querySelector('svg')).not.toBeNull();
    expect(chip?.textContent).toContain(episodesVi.status.story_review);
    expect(chip?.getAttribute('aria-label')).toBe(episodesVi.status.story_review);
  });

  it('RelativeTime_UsesIntl_WithAbsoluteTitle', async () => {
    const { fixture, root } = await setup();
    const time = root.querySelector('time');
    const expectedRelative = new Intl.RelativeTimeFormat('vi-VN', { numeric: 'auto' }).format(-1, 'hour');
    expect(time?.textContent).toContain(expectedRelative);
    expect(time?.getAttribute('title')).toBe(
      TestBed.inject(LocaleService).formatDateTime('2026-10-05T09:00:00Z'),
    );

    TestBed.inject(LocaleService).activate('en', { persist: false });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(time?.textContent).toContain(
      new Intl.RelativeTimeFormat('en-US', { numeric: 'auto' }).format(-1, 'hour'),
    );
  });

  it('Open_EmitsId', async () => {
    const { root, component } = await setup();
    const ids: string[] = [];
    onEmit(component.open, (id) => ids.push(id));
    root.querySelector<HTMLButtonElement>('.next-action-row__title')?.click();
    expect(ids).toEqual([viewModel().episode.id]);
  });

  it('RendersRuledListItem_NotMaterialCard', async () => {
    await TestBed.configureTestingModule({
      imports: [RowHost, translations],
      providers: [...ZONELESS_TEST_PROVIDERS],
    }).compileComponents();
    const fixture = TestBed.createComponent(RowHost);
    fixture.componentRef.setInput('vm', viewModel());
    fixture.componentRef.setInput('now', new Date('2026-10-05T10:00:00Z').getTime());
    fixture.detectChanges();
    await fixture.whenStable();
    const row = (fixture.nativeElement as HTMLElement).querySelector('li.next-action-row');
    expect(row).not.toBeNull();
    expect(row?.querySelector('mat-card')).toBeNull();
  });
});
