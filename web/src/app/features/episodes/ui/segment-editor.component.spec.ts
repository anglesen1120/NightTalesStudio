import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import qualityEn from '../../../../assets/i18n/quality/en.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import type { SimilarityLevel } from '../domain/audit.model';
import { SegmentEditorComponent } from './segment-editor.component';
import type { SegmentEditorViewModel } from './story-editor.view-model';

const segment: SegmentEditorViewModel = {
  address: 'p:3', segKey: 'paragraph-4', text: 'A remembered sound.', tokens: 3, labelKey: 'episodes.editor.segment.paragraph',
  labelIndex: 4, beat: null, keywords: [], errorKeys: [], readOnly: false, describedById: 'segment-feedback',
};
async function setup(level: SimilarityLevel | null) {
  await TestBed.configureTestingModule({
    imports: [SegmentEditorComponent, TranslocoTestingModule.forRoot({
      langs: { en: {}, 'episodes/en': episodesEn, 'quality/en': qualityEn, 'errors/en': errorsEn },
      translocoConfig: { availableLangs: ['en'], defaultLang: 'en', fallbackLang: 'en' }, preloadLangs: true,
    })], providers: ZONELESS_TEST_PROVIDERS,
  }).compileComponents();
  const fixture = TestBed.createComponent(SegmentEditorComponent);
  fixture.componentRef.setInput('viewModel', segment);
  fixture.componentRef.setInput('similarityLevel', level);
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement };
}
afterEach(() => TestBed.resetTestingModule());

describe('SegmentEditorComponent similarity decoration', () => {
  it.each([
    ['warn', qualityEn.similarity_warn],
    ['block', qualityEn.similarity_blocked],
  ] as const)('links the %s textual note to the textarea without inventing an annotation row', async (level, copy) => {
    const { root } = await setup(level);
    const note = root.querySelector<HTMLElement>('#seg-p-3-similarity')!;
    expect(note.textContent).toBe(copy);
    expect(root.querySelector(`.segment-editor--similarity-${level}`)).not.toBeNull();
    expect(root.querySelector('textarea')?.getAttribute('aria-describedby')?.split(' ')).toContain(note.id);
    expect(root.querySelector('textarea')?.getAttribute('aria-describedby')).toContain('seg-p-3-counter');
    expect(root.querySelectorAll('nts-inline-annotation')).toHaveLength(0);
  });

  it('removes both decorative outline and description when the supplied level clears', async () => {
    const { root, fixture } = await setup('warn');
    fixture.componentRef.setInput('similarityLevel', null);
    await fixture.whenStable();
    expect(root.querySelector('.segment-editor__similarity')).toBeNull();
    expect(root.querySelector('.segment-editor--similarity-warn, .segment-editor--similarity-block')).toBeNull();
    expect(root.querySelector('textarea')?.getAttribute('aria-describedby')).toBe('seg-p-3-counter');
  });
});
