import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import type { SegmentAddress } from '@shared/util/segment-address';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { StoryEditorComponent } from './story-editor.component';
import type { SegmentEditorViewModel, StoryEditorViewModel } from './story-editor.view-model';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: episodesVi, 'episodes/vi': episodesVi, 'errors/vi': errorsVi },
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
    totalTokens: 12,
    readOnly: false,
    fieldErrors: {},
    formErrors: [],
    keywordLimitHit: null,
    saveBar: { state: 'dirty', disabled: false },
  };
}

async function setup(vm = viewModel()) {
  await TestBed.configureTestingModule({ imports: [StoryEditorComponent, translations], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(StoryEditorComponent);
  fixture.componentRef.setInput('viewModel', vm);
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

afterEach(() => TestBed.resetTestingModule());

describe('StoryEditorComponent', () => {
  it('renders beats as read-only context and one textarea for every paragraph', async () => {
    const { root } = await setup();
    expect(root.textContent).toContain('Nhịp: Gieo chuyện');
    expect(root.querySelectorAll('textarea[data-segment^="p:"]')).toHaveLength(2);
    expect(root.querySelectorAll('input[value="Gieo chuyện"]')).toHaveLength(0);
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
});
