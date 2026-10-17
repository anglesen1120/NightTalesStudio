import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { By } from '@angular/platform-browser';
import type { SegmentAddress } from '@shared/util/segment-address';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import qualityVi from '../../../../assets/i18n/quality/vi.json';
import { StoryEditorComponent } from './story-editor.component';
import type { SegmentEditorViewModel, StoryEditorViewModel } from './story-editor.view-model';
import { SceneStripComponent } from './scene-strip.component';
import { SegmentNavComponent } from './segment-nav.component';
import { AnnotationPanelComponent } from './annotation-panel.component';
import { SegmentEditorComponent } from './segment-editor.component';
import { buildAnnotationViews } from './annotation.view-model';
import type { SegmentAnnotation } from '../domain/annotation.model';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: episodesVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi, 'quality/vi': qualityVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});

function segment(address: SegmentAddress, segKey: string, text: string, tokens: number, labelIndex: number | null = null, beat: string | null = null): SegmentEditorViewModel {
  return { address, segKey, text, tokens, labelIndex, beat, keywords: address === 'title' ? [] : ['Mưa'], errorKeys: [], readOnly: false, labelKey: `episodes.editor.segment.${labelIndex === null ? address : 'paragraph'}`, describedById: `seg-${address.replace(':', '-')}-feedback` };
}

function viewModel(paragraphs = 2): StoryEditorViewModel {
  return {
    title: segment('title', 'title', 'Chuyến xe', 2),
    hook: segment('hook', 'hook-key', 'Mở màn', 2),
    paragraphs: Array.from({ length: paragraphs }, (_, index) => segment(`p:${index}`, `paragraph-${index}`, `Đoạn ${index + 1}`, index + 3, index + 1, index === 0 ? 'Gieo chuyện' : 'Dồn dập')),
    ending: segment('ending', 'ending-key', 'Kết truyện', 2),
    meta: { title: 'Chuyến xe', description: 'Mô tả ngắn', tags: ['ma'], contentWarnings: [], structure: null },
    metaTokens: 3, totalTokens: 12,
    readOnly: false,
    fieldErrors: {},
    formErrors: [],
    keywordLimitHit: null, structureWarning: false,
    saveBar: { state: 'dirty', disabled: false, errorKey: null },
  };
}

async function setup(vm = viewModel()) {
  await TestBed.configureTestingModule({ imports: [StoryEditorComponent, translations], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(StoryEditorComponent);
  fixture.componentRef.setInput('viewModel', vm);
  const texts = new Map([vm.hook, ...vm.paragraphs, vm.ending].map((segment) => [segment.address, signal(segment.text)] as const));
  fixture.componentRef.setInput('segmentTexts', texts);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance, texts };
}

afterEach(() => TestBed.resetTestingModule());

describe('StoryEditorComponent', () => {
  it('passes similarity levels by address through every editor without rebuilding the map on a text edit', async () => {
    const initial = viewModel();
    const { fixture, root, texts } = await setup(initial);
    const highlights = new Map<SegmentAddress, 'warn' | 'block'>([['hook', 'warn'], ['p:1', 'block'], ['ending', 'warn']]);
    fixture.componentRef.setInput('similarityHighlights', highlights);
    fixture.detectChanges(); await fixture.whenStable();
    const levels = (): Record<string, string | null> => Object.fromEntries(fixture.debugElement.queryAll(By.directive(SegmentEditorComponent))
      .map((element) => {
        const segment = element.componentInstance as SegmentEditorComponent;
        return [segment.viewModel().address, segment.similarityLevel()];
      }));
    expect(levels()).toEqual({ title: null, hook: 'warn', 'p:0': null, 'p:1': 'block', ending: 'warn' });
    expect(root.querySelectorAll('.segment-editor__similarity')).toHaveLength(3);
    texts.get('p:0')!.set('Typed text');
    fixture.componentRef.setInput('viewModel', { ...initial, paragraphs: [{ ...initial.paragraphs[0]!, text: 'Typed text' }, initial.paragraphs[1]!] });
    fixture.detectChanges(); await fixture.whenStable();
    expect(fixture.componentInstance.similarityHighlights()).toBe(highlights);
    expect(levels()['p:1']).toBe('block');
    fixture.componentRef.setInput('similarityHighlights', new Map());
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.segment-editor__similarity')).toBeNull();
  });

  it('renders beats as read-only context and one textarea for every paragraph', async () => {
    const { root } = await setup();
    expect(root.textContent).toContain('Nhịp: Gieo chuyện');
    expect(root.querySelectorAll('textarea[data-segment^="p:"]')).toHaveLength(2);
    expect(root.querySelectorAll('input[value="Gieo chuyện"]')).toHaveLength(0);
  });

  it('shows the structure advisory without blocking Save and keeps every paragraph reachable', async () => {
    const { root } = await setup({ ...viewModel(4), structureWarning: true });
    expect(root.querySelector('.story-editor__structure-warning')?.textContent).toContain(episodesVi.editor.structureWarning);
    expect(root.querySelector<HTMLButtonElement>('.story-save-bar button')?.disabled).toBe(false);
    const areas = [...root.querySelectorAll<HTMLTextAreaElement>('textarea[data-segment^="p:"]')];
    expect(areas).toHaveLength(4);
    expect(areas.every((area) => area.tabIndex === 0 && !area.hidden)).toBe(true);
    expect(root.querySelector('nts-segment-nav select')).not.toBeNull();
  });

  it('forwards structural actions from the composed cards', async () => {
    const { root, component } = await setup();
    const intents: unknown[] = [];
    onEmit(component.intent, (intent) => intents.push(intent));
    root.querySelector<HTMLButtonElement>('[data-action="move-down"]')!.click();
    root.querySelector<HTMLButtonElement>('[data-action="delete"]')!.click();
    expect(intents).toEqual([{ kind: 'moveParagraph', from: 0, to: 1 }, { kind: 'removeParagraph', index: 0 }]);
  });

  it('tracks paragraphs by segKey so appending does not recreate existing textareas', async () => {
    const initial = viewModel(2);
    const { fixture, root } = await setup(initial);
    const first = root.querySelector<HTMLTextAreaElement>('textarea[data-segment="p:0"]');
    fixture.componentRef.setInput('viewModel', viewModel(3));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector<HTMLTextAreaElement>('textarea[data-segment="p:0"]')).toBe(first);
    expect(root.querySelectorAll('textarea[data-segment^="p:"]')).toHaveLength(3);
  });

  it('wires per-segment and total token counters', async () => {
    const { root } = await setup();
    expect(root.querySelector('#seg-p-0-counter')?.textContent).toContain('3 từ');
    expect(root.querySelector('.story-save-bar__total')?.textContent).toContain('Tổng cộng 12 từ');
  });

  it('focuses and scrolls the canonical textarea selected by a deep link', async () => {
    const { root, component } = await setup(viewModel(4));
    const target = root.querySelector<HTMLTextAreaElement>('#seg-p-3')!;
    Object.defineProperty(target, 'scrollIntoView', { configurable: true, value: () => undefined });
    expect(component.focusSegment('p:3')).toBe(true);
    expect(document.activeElement).toBe(target);
  });

  it('keeps the scene strip read-only', async () => {
    const { root } = await setup();
    const strip = root.querySelector('[data-testid="scene-strip"]')!;
    expect(strip.querySelectorAll('input, textarea, button, select')).toHaveLength(0);
  });
  it('keeps nav and scene structure inputs stable during a single text edit', async () => {
    const initial = viewModel();
    const { fixture, root, texts } = await setup(initial);
    const nav = fixture.debugElement.query(By.directive(SegmentNavComponent)).componentInstance as SegmentNavComponent;
    const strip = fixture.debugElement.query(By.directive(SceneStripComponent)).componentInstance as SceneStripComponent;
    const navBefore = nav.segments();
    const scenesBefore = strip.segments();
    const changed = { ...initial.paragraphs[0]!, text: 'Đoạn 1!' };
    texts.get('p:0')!.set(changed.text);
    fixture.componentRef.setInput('viewModel', { ...initial, paragraphs: [changed, initial.paragraphs[1]!] });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(nav.segments()).toBe(navBefore);
    expect(strip.segments()).toBe(scenesBefore);
    expect(root.querySelector('[data-testid="scene-strip"]')?.textContent).toContain('Đoạn 1!');

    fixture.componentRef.setInput('viewModel', { ...initial, paragraphs: [{ ...changed, keywords: ['Sương'] }, initial.paragraphs[1]!] });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(nav.segments()).toBe(navBefore);
    expect(strip.segments()).not.toBe(scenesBefore);
    expect(root.querySelector('[data-testid="scene-strip"]')?.textContent).toContain('Sương');
  });

  it('announces the mapped error rather than an unknown error, then the unsaved draft', async () => {
    const initial = viewModel();
    const { fixture, root } = await setup({ ...initial, saveBar: { state: 'error', disabled: false, errorKey: 'errors.story_invalid_content' }, formErrors: ['errors.story_invalid_content'] });
    expect(root.querySelectorAll('[role="alert"]')).toHaveLength(1);
    expect(root.querySelector('[role="status"]')?.textContent).toContain(errorsVi.story_invalid_content);
    fixture.componentRef.setInput('viewModel', initial);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(episodesVi.editor.unsaved);
  });

  it('tolerates duplicate field errors, tags and warnings without duplicate track keys', async () => {
    const initial = viewModel();
    const warn = vi.spyOn(console, 'warn');
    const error = vi.spyOn(console, 'error');
    try {
      const { root } = await setup({
        ...initial,
        hook: { ...initial.hook, errorKeys: ['errors.story_invalid_content', 'errors.story_invalid_content'] },
        fieldErrors: { meta: ['errors.story_invalid_content', 'errors.story_invalid_content'] },
        formErrors: ['errors.story_invalid_content', 'errors.story_invalid_content'],
        meta: { ...initial.meta, tags: ['ma', 'ma'], contentWarnings: ['bạo lực', 'bạo lực'] },
      });
      expect(root.querySelectorAll('#seg-hook-errors li')).toHaveLength(2);
      expect(root.querySelectorAll('#seg-meta-errors li')).toHaveLength(2);
      expect(root.querySelectorAll('.story-meta__readonly li')).toHaveLength(4);
      expect(root.querySelectorAll('.story-editor__form-errors li')).toHaveLength(2);
      expect([...warn.mock.calls, ...error.mock.calls].flat().join(' ')).not.toContain('NG0955');
    } finally {
      warn.mockRestore();
      error.mockRestore();
    }
  });

  it('passes annotation slices through paragraph cards, links the controls, and keeps the sole annotation live region outside the deferred panel', async () => {
    const { fixture, root, component } = await setup();
    const annotation: SegmentAnnotation = {
      id: 'ann-p-0-Q-LEN-01', source: 'hint', address: 'p:0', ruleId: 'Q-LEN-01', severity: 'blocking', kind: 'objective',
      messageKey: 'episodes.editor.structureWarning', evidence: 'Saved text', repairDirection: null, keywordIndex: null, placement: 'inline',
    };
    const keyword: SegmentAnnotation = { ...annotation, id: 'ann-p-1-invalid-k0', address: 'p:1', source: 'keyword', keywordIndex: 0, messageKey: 'episodes.editor.keywordFlaggedHint' };
    const meta: SegmentAnnotation = { ...annotation, id: 'ann-meta-sparse', address: 'meta', placement: 'panel' };
    const views = buildAnnotationViews([annotation, keyword, meta], { hasKey: () => true });
    fixture.componentRef.setInput('annotationViews', views);
    fixture.componentRef.setInput('announcement', { text: '3 suggestions', sequence: 1 });
    fixture.detectChanges(); await fixture.whenStable();
    const textarea = root.querySelector<HTMLTextAreaElement>('textarea[data-segment="p:0"]')!;
    expect(textarea.getAttribute('aria-describedby')).toContain(annotation.id);
    expect(textarea.nextElementSibling?.tagName).toBe('NTS-INLINE-ANNOTATION');
    const flagged = root.querySelector<HTMLInputElement>('textarea[data-segment="p:1"]')!.closest('section')!.querySelector<HTMLInputElement>('input')!;
    expect(flagged.getAttribute('aria-invalid')).toBe('true');
    expect(flagged.getAttribute('aria-describedby')).toBe(keyword.id);
    expect(root.querySelector('.story-editor__sheet #ann-meta-sparse')).toBeNull();
    expect(root.querySelectorAll('.story-editor__annotation-announcement[aria-live="polite"]')).toHaveLength(1);
    expect(root.querySelector('.story-editor__annotation-announcement')?.textContent).toBe('3 suggestions');
    expect(root.querySelector('nts-annotation-panel [aria-live]')).toBeNull();
    // A new sequence re-creates the announced node, so a repeated count still mutates the live region.
    const announced = root.querySelector('.story-editor__annotation-announcement span');
    fixture.componentRef.setInput('announcement', { text: '3 suggestions', sequence: 2 });
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.story-editor__annotation-announcement span')).not.toBe(announced);
    const emitted: unknown[] = [];
    onEmit(component.intent, (intent) => emitted.push(intent));
    // The inline note sits at the segment it is about and offers no navigation button; the panel item is the way back.
    expect(root.querySelector('#ann-p-0-Q-LEN-01 button')).toBeNull();
    const panel = fixture.debugElement.query(By.directive(AnnotationPanelComponent)).componentInstance as AnnotationPanelComponent;
    expect(panel.viewModel()).toBe(views.panel);
    panel.focusRequested.emit(views.panel.groups[0]!.items[0]!);
    expect(emitted).toEqual([{ kind: 'openSegment', address: 'p:0' }]);
    const segment = fixture.debugElement.queryAll(By.directive(SegmentEditorComponent)).map((element) => element.componentInstance as SegmentEditorComponent).find((item) => item.viewModel().address === 'p:0')!;
    expect(segment.annotations()).toBe(views.inline.get('p:0'));
    const initialText = viewModel();
    fixture.componentRef.setInput('viewModel', { ...initialText, paragraphs: [{ ...initialText.paragraphs[0]!, text: 'Typing' }, initialText.paragraphs[1]!] });
    fixture.detectChanges();
    expect(segment.annotations()).toBe(views.inline.get('p:0'));
  });

});
