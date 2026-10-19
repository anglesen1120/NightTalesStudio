import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import episodesEn from '../../../../../assets/i18n/episodes/en.json';
import episodesVi from '../../../../../assets/i18n/episodes/vi.json';
import { QualityNotesComponent } from './quality-notes.component';

async function setup(codes: readonly string[], lang: 'vi' | 'en' = 'en') {
  await TestBed.configureTestingModule({ imports: [QualityNotesComponent, TranslocoTestingModule.forRoot({ langs: { en: { episodes: episodesEn }, vi: { episodes: episodesVi } }, translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(QualityNotesComponent);
  fixture.componentRef.setInput('codes', codes);
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

afterEach(() => TestBed.resetTestingModule());

describe('QualityNotesComponent', () => {
  it.each(['vi', 'en'] as const)('renders four known hints and no unknown code in %s', async (lang) => {
    const root = await setup(['subtitle_sync_suspect', 'subtitle_cues_long', 'music_none', 'scenes_trimmed', 'v9', 'constructor'], lang);
    const copy = (lang === 'vi' ? episodesVi : episodesEn).render;
    expect([...root.querySelectorAll('li')].map((item) => item.textContent?.trim())).toEqual([copy.quality.subtitleSyncSuspect, copy.quality.subtitleCuesLong, copy.quality.musicNone, copy.quality.scenesTrimmed]);
    expect(root.textContent).not.toMatch(/v9|constructor/);
    expect(root.textContent).toContain(copy.review.advisory);
    expect(root.querySelector('ul')?.closest('section')?.getAttribute('aria-labelledby')).toBe(root.querySelector('h3')?.id);
    expect(root.querySelectorAll('nts-icon-alert')).toHaveLength(4);
  });

  it.each([{ codes: [] }, { codes: ['private-provider-detail'] }])('renders the no-notes message for $codes', async ({ codes }) => {
    const root = await setup(codes);
    expect(root.querySelector('li')).toBeNull();
    expect(root.textContent).toContain(episodesEn.render.review['hints.none']);
    expect(root.textContent).toContain(episodesEn.render.review.advisory);
    expect(root.textContent).not.toContain('private-provider-detail');
  });

  it('accepts scenes_sparse without repeating duplicate notes', async () => {
    const root = await setup(['scenes_sparse', 'scenes_sparse']);
    expect(root.querySelectorAll('li')).toHaveLength(1);
    expect(root.textContent).toContain(episodesEn.render.quality.scenesSparse);
    expect(root.querySelector('[disabled], [aria-disabled]')).toBeNull();
  });
});
