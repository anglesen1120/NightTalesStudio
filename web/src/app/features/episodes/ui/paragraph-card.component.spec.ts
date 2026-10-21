import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { onEmit } from '@shared/ui/testing/on-emit';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import qualityVi from '../../../../assets/i18n/quality/vi.json';
import qualityEn from '../../../../assets/i18n/quality/en.json';
import { ParagraphCardComponent } from './paragraph-card.component';
import type { SegmentEditorViewModel, StoryEditorIntent } from './story-editor.view-model';

function paragraph(index: number, readOnly = false): SegmentEditorViewModel {
  return { address: `p:${index}`, segKey: 'stable-key', labelKey: 'episodes.editor.segment.paragraph', labelIndex: index + 1,
    beat: 'setup', text: '<script>not markup</script>', keywords: ['rain', 'street'], tokens: 3, errorKeys: [], readOnly, describedById: `seg-p-${index}-feedback` };
}

async function setup(index = 2, count = 4, readOnly = false, lang: 'vi' | 'en' = 'vi') {
  const translations = TranslocoTestingModule.forRoot({
    langs: { vi: episodesVi, en: episodesEn, 'episodes/vi': episodesVi, 'episodes/en': episodesEn, 'quality/vi': qualityVi, 'quality/en': qualityEn },
    translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true }, preloadLangs: true,
  });
  await TestBed.configureTestingModule({ imports: [ParagraphCardComponent, translations], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(ParagraphCardComponent);
  fixture.componentRef.setInput('viewModel', paragraph(index, readOnly));
  fixture.componentRef.setInput('index', index);
  fixture.componentRef.setInput('count', count);
  const intents: StoryEditorIntent[] = [];
  onEmit(fixture.componentInstance.intent, (intent) => intents.push(intent));
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, intents };
}

afterEach(() => TestBed.resetTestingModule());

describe('ParagraphCardComponent', () => {
  it('passes a similarity level to the segment editor and removes the note when cleared', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('similarityLevel', 'warn');
    fixture.detectChanges(); await fixture.whenStable();
    const area = root.querySelector('textarea')!;
    expect(root.querySelector('.segment-editor--similarity-warn')).not.toBeNull();
    expect(root.querySelector('.segment-editor__similarity')?.textContent).toBe(qualityVi.similarity_warn);
    expect(area.getAttribute('aria-describedby')).toContain('similarity');
    fixture.componentRef.setInput('similarityLevel', null);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.segment-editor__similarity')).toBeNull();
    expect(area.getAttribute('aria-describedby')).not.toContain('similarity');
  });

  for (const [lang, up, down, handle, del, delAt] of [
    ['vi', 'Lên: đoạn 3', 'Xuống: đoạn 3', 'Kéo để di chuyển', 'Xoá đoạn', 'Xoá đoạn 3'],
    ['en', 'Move up: paragraph 3', 'Move down: paragraph 3', 'Drag to move', 'Delete paragraph', 'Delete paragraph 3'],
  ] as const) {
    it(`labels pointer and keyboard controls with safe positions in ${lang}`, async () => {
      const { root } = await setup(2, 4, false, lang);
      expect(root.querySelector('[data-action="move-up"]')?.getAttribute('aria-label')).toBe(up);
      expect(root.querySelector('[data-action="move-down"]')?.getAttribute('aria-label')).toBe(down);
      // The visible label leads the accessible name, so the position is announced without hiding the visible text
      // from speech input (WCAG 2.5.3 label-in-name).
      const remove = root.querySelector<HTMLButtonElement>('[data-action="delete"]')!;
      expect(remove.textContent?.trim()).toBe(del);
      expect(remove.getAttribute('aria-label')).toBe(delAt);
      expect(remove.getAttribute('aria-label')?.startsWith(del)).toBe(true);
      expect(root.querySelector('.paragraph-card__handle')?.getAttribute('aria-label')).toBe(handle);
      expect(root.querySelector('.paragraph-card__handle')?.hasAttribute('aria-hidden')).toBe(false);
      expect(root.querySelector<HTMLTextAreaElement>('textarea')?.value).toBe('<script>not markup</script>');
      expect(root.querySelector('script')).toBeNull();
    });
  }

  it('keeps unavailable end buttons in the tab order and ignores activation', async () => {
    const { fixture, root, intents } = await setup(0, 4);
    const up = root.querySelector<HTMLButtonElement>('[data-action="move-up"]')!;
    expect(up.getAttribute('aria-disabled')).toBe('true');
    expect(up.disabled).toBe(false);
    expect(up.tabIndex).toBe(0);
    up.focus();
    expect(document.activeElement).toBe(up);
    up.click();
    expect(intents).toEqual([]);
    fixture.componentRef.setInput('index', 3);
    fixture.componentRef.setInput('viewModel', paragraph(3));
    fixture.detectChanges();
    await fixture.whenStable();
    const down = root.querySelector<HTMLButtonElement>('[data-action="move-down"]')!;
    expect(down.getAttribute('aria-disabled')).toBe('true');
    expect(down.disabled).toBe(false);
    expect(down.tabIndex).toBe(0);
    down.click();
    expect(intents).toEqual([]);
    up.click();
    expect(intents).toEqual([{ kind: 'moveParagraph', from: 3, to: 2 }]);
  });

  it('emits the current index for deletion and keeps beat and editable chips in the card', async () => {
    const { root, intents } = await setup();
    expect(root.querySelector('.segment-editor__beat')?.textContent).toContain('setup');
    const chips = root.querySelector('nts-segment-chips')!;
    expect([...chips.querySelectorAll<HTMLInputElement>('input')].map((input) => input.value)).toEqual(['rain', 'street']);
    root.querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
    expect(intents).toEqual([{ kind: 'removeParagraph', index: 2 }]);
  });

  it('uses the existing segment editor for text and keyword edits', async () => {
    const { root, intents } = await setup();
    const area = root.querySelector<HTMLTextAreaElement>('textarea')!;
    area.value = 'Edited paragraph';
    area.dispatchEvent(new Event('input', { bubbles: true }));
    const keyword = root.querySelector<HTMLInputElement>('nts-segment-chips input')!;
    keyword.value = 'fog';
    keyword.dispatchEvent(new Event('input', { bubbles: true }));
    expect(intents).toEqual([
      { kind: 'editText', segKey: 'stable-key', text: 'Edited paragraph' },
      { kind: 'editKeyword', segKey: 'stable-key', op: 'update', index: 0, value: 'fog' },
    ]);
  });

  it('allows deleting the last paragraph while both reorder buttons stay unavailable', async () => {
    const { root, intents } = await setup(0, 1);
    for (const action of ['move-up', 'move-down']) {
      const button = root.querySelector<HTMLButtonElement>(`[data-action="${action}"]`)!;
      expect(button.getAttribute('aria-disabled')).toBe('true');
      button.click();
    }
    expect(intents).toEqual([]);
    root.querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
    expect(intents).toEqual([{ kind: 'removeParagraph', index: 0 }]);
  });

  it('hides every structure action in read-only state', async () => {
    const { root } = await setup(1, 3, true);
    expect(root.querySelectorAll('.paragraph-card__actions button, .paragraph-card__handle')).toHaveLength(0);
    expect(root.querySelector<HTMLTextAreaElement>('textarea')?.readOnly).toBe(true);
  });

  it('also hides structure actions when the enclosing list is read-only', async () => {
    const { fixture, root } = await setup();
    fixture.componentRef.setInput('readOnly', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelectorAll('.paragraph-card__actions button, .paragraph-card__handle')).toHaveLength(0);
  });
});
