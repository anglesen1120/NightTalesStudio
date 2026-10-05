import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import { EPISODE_STATUSES, nextActionFor } from '../domain/episode.model';
import { episodeItem } from '../testing/fake-episodes.repository';
import { EpisodeRowComponent } from './episode-row.component';

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

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class EpisodeDetailStub {}

async function setup() {
  await TestBed.configureTestingModule({
    imports: [EpisodeRowComponent, translations],
    providers: [
      ...ZONELESS_TEST_PROVIDERS,
      provideRouter([{ path: 'episodes/:id', component: EpisodeDetailStub }]),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(EpisodeRowComponent);
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

describe('EpisodeRowComponent', () => {
  it('renders an icon-and-text chip for every one of the 11 statuses', async () => {
    const { fixture, root } = await setup();
    for (const status of EPISODE_STATUSES) {
      fixture.componentRef.setInput('item', episodeItem(1, { status }));
      fixture.detectChanges();
      await fixture.whenStable();
      const chip = root.querySelector('.episode-row__status');
      expect(chip, status).not.toBeNull();
      const expected = episodesVi.status[status];
      expect(chip?.getAttribute('aria-label')).toBe(expected);
      expect(chip?.textContent).toContain(expected);
      expect(chip?.querySelector('svg'), `status ${status} carries an inline icon`).not.toBeNull();
      expect(root.querySelector('.episode-row__continue')?.getAttribute('href')).toBe('/episodes/ep-1');
      expect(root.querySelector('.episode-row__continue')?.textContent?.trim()).toBe('Tiếp tục');
    }
  });

  it('shows the generating chip (clock icon + text) for story_generating', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(2, { status: 'story_generating' }));
    fixture.detectChanges();
    await fixture.whenStable();
    const chip = root.querySelector('.episode-row__status');
    expect(chip?.querySelector('nts-icon-clock')).not.toBeNull();
    expect(chip?.textContent).toContain('Đang viết');
  });

  it('labels Continue per language', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(3));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__continue')?.textContent?.trim()).toBe('Tiếp tục');

    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__continue')?.textContent?.trim()).toBe('Continue');
  });

  it('renders the next-action contract label and detail target for each status', async () => {
    const { fixture, root, component } = await setup();
    // Angular bundles ESM exports as non-configurable properties. Supply an alternate action at the row's
    // computed-view-model boundary to prove the rendered label follows the contract rather than a literal key.
    const action = signal({ labelKey: 'episodes.list.untitled', segment: 'detail' as const });
    Object.defineProperty(component, 'nextAction', { value: action });
    for (const [index, status] of EPISODE_STATUSES.entries()) {
      fixture.componentRef.setInput('item', episodeItem(index + 1, { status }));
      action.set({ ...nextActionFor(status), labelKey: 'episodes.list.untitled' });
      fixture.detectChanges();
      await fixture.whenStable();

      expect(root.querySelector('.episode-row__continue')?.textContent?.trim()).toBe('Chưa đặt tên');
      expect(root.querySelector('.episode-row__continue')?.getAttribute('href')).toBe(`/episodes/ep-${index + 1}`);
    }

    action.set(nextActionFor('failed'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__continue')?.textContent?.trim()).toBe('Tiếp tục');
  });

  it('links the title and Continue to the detail route and emits the id on open', async () => {
    const { fixture, root, component } = await setup();
    fixture.componentRef.setInput('item', episodeItem(7));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(root.querySelector('.episode-row__title')?.getAttribute('href')).toBe('/episodes/ep-7');
    expect(root.querySelector('.episode-row__continue')?.getAttribute('href')).toBe('/episodes/ep-7');

    const opened: string[] = [];
    onEmit(component.open, (id) => opened.push(id));
    root.querySelector<HTMLAnchorElement>('.episode-row__continue')?.click();
    expect(opened).toEqual(['ep-7']);
  });

  it('falls back to the untitled copy for a null title', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(4, { title: null }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__title')?.textContent).toContain('Chưa đặt tên');
  });

  it('updates the blank-title fallback on language change and preserves usable titles', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('item', episodeItem(4, { title: ' \t\n ' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__title')?.textContent?.trim()).toBe('Chưa đặt tên');

    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__title')?.textContent?.trim()).toBe(episodesEn.list.untitled);

    fixture.componentRef.setInput('item', episodeItem(4, { title: '  A moonlit story  ' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('.episode-row__title')?.textContent).toBe('A moonlit story');
  });

  it('formats updatedAt with the shared Intl.DateTimeFormat of the active language', async () => {
    const { fixture, root } = await setup();
    const updatedAt = '2026-10-02T10:00:00Z';
    fixture.componentRef.setInput('item', episodeItem(5, { updatedAt }));
    fixture.detectChanges();
    await fixture.whenStable();
    const time = root.querySelector('time');
    expect(time?.getAttribute('datetime')).toBe(updatedAt);
    expect(time?.textContent?.trim()).toBe(
      new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(updatedAt)),
    );

    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(time?.textContent?.trim()).toBe(
      new Intl.DateTimeFormat('en-US', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(updatedAt)),
    );
  });
});
