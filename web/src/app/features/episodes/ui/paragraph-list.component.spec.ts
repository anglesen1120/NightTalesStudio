import { LiveAnnouncer } from '@angular/cdk/a11y';
import { CdkDrag, CdkDropList, CdkDragHandle } from '@angular/cdk/drag-drop';
import type { CdkDragDrop } from '@angular/cdk/drag-drop';
import { TestBed } from '@angular/core/testing';
import type { ComponentFixture } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import { ParagraphListComponent } from './paragraph-list.component';
import type { SegmentEditorViewModel, StoryEditorIntent } from './story-editor.view-model';

class FakeAnnouncer {
  readonly messages: { readonly text: string; readonly focusAddress: string | null; readonly focusText: string | null }[] = [];
  announce(text: string): Promise<void> {
    const active = document.activeElement;
    this.messages.push({ text, focusAddress: active?.getAttribute('data-segment') ?? null, focusText: active instanceof HTMLTextAreaElement ? active.value : null });
    return Promise.resolve();
  }
}

function paragraphs(): readonly SegmentEditorViewModel[] {
  return ['First', 'Second', 'Third'].map<SegmentEditorViewModel>((text, index) => ({ address: `p:${index}`, segKey: `key-${index}`, labelKey: 'episodes.editor.segment.paragraph', labelIndex: index + 1,
    beat: `beat-${index}`, text, keywords: [`scene-${index}`], tokens: 1, errorKeys: [], readOnly: false, describedById: `seg-p-${index}-feedback` }));
}

async function setup(pointerReorder = true, lang: 'vi' | 'en' = 'vi', applyMoves = true): Promise<ParagraphListFixture> {
  const translations = TranslocoTestingModule.forRoot({
    langs: { vi: episodesVi, en: episodesEn, 'episodes/vi': episodesVi, 'episodes/en': episodesEn },
    translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: lang, fallbackLang: lang, reRenderOnLangChange: true }, preloadLangs: true,
  });
  const announcer = new FakeAnnouncer();
  await TestBed.configureTestingModule({ imports: [ParagraphListComponent, translations], providers: [...ZONELESS_TEST_PROVIDERS, { provide: LiveAnnouncer, useValue: announcer }] }).compileComponents();
  const fixture = TestBed.createComponent(ParagraphListComponent);
  fixture.componentRef.setInput('paragraphs', paragraphs());
  fixture.componentRef.setInput('pointerReorder', pointerReorder);
  const intents: StoryEditorIntent[] = [];
  onEmit(fixture.componentInstance.intent, (intent) => {
    intents.push(intent);
    if (intent.kind !== 'moveParagraph' || !applyMoves) return;
    const next = [...fixture.componentInstance.paragraphs()];
    const moved = next.splice(intent.from, 1)[0]!;
    next.splice(intent.to, 0, moved);
    fixture.componentRef.setInput('paragraphs', next.map((paragraph, index) => ({ ...paragraph, address: `p:${index}`, labelIndex: index + 1 })));
  });
  fixture.detectChanges();
  await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, intents, announcer };
}

interface ParagraphListFixture {
  readonly fixture: ComponentFixture<ParagraphListComponent>;
  readonly root: HTMLElement;
  readonly intents: StoryEditorIntent[];
  readonly announcer: FakeAnnouncer;
}
function drop({ fixture }: ParagraphListFixture, from: number, to: number, inside = true): void {
  const list = fixture.debugElement.query(By.directive(CdkDropList));
  const container = list.injector.get(CdkDropList) as CdkDropList<readonly SegmentEditorViewModel[]>;
  const item = fixture.debugElement.queryAll(By.directive(CdkDrag))[from]!.injector.get(CdkDrag) as CdkDrag<string>;
  const event: CdkDragDrop<readonly SegmentEditorViewModel[]> = { previousIndex: from, currentIndex: to, item, container, previousContainer: container,
    isPointerOverContainer: inside, distance: { x: 0, y: 10 }, dropPoint: { x: 0, y: 10 }, event: new MouseEvent('mouseup') };
  list.triggerEventHandler('cdkDropListDropped', event);
}

afterEach(() => TestBed.resetTestingModule());

describe('ParagraphListComponent', () => {
  it('emits a drop move and preserves the paragraph node, beat and chips at its new address', async () => {
    const result = await setup();
    const { fixture, root, intents, announcer } = result;
    const before = [...root.querySelectorAll('nts-paragraph-card')];
    const area = before[1]!.querySelector('textarea');
    drop(result, 1, 0);
    expect(announcer.messages).toEqual([]);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(intents).toEqual([{ kind: 'moveParagraph', from: 1, to: 0 }]);
    const after = [...root.querySelectorAll('nts-paragraph-card')];
    expect(after).toEqual([before[1], before[0], before[2]]);
    expect(after[0]!.querySelector('textarea')).toBe(area);
    expect(after[0]!.querySelector('.segment-editor__beat')?.textContent).toContain('beat-1');
    expect(after[0]!.querySelector<HTMLInputElement>('nts-segment-chips input')?.value).toBe('scene-1');
    expect(announcer.messages).toEqual([{ text: 'Đã chuyển đoạn 2 tới vị trí 1', focusAddress: 'p:0', focusText: 'Second' }]);
  });

  it('ignores drops on itself and outside the list without emitting or announcing', async () => {
    const result = await setup();
    drop(result, 1, 1);
    drop(result, 1, 0, false);
    result.fixture.detectChanges();
    await result.fixture.whenStable();
    expect(result.intents).toEqual([]);
    expect(result.announcer.messages).toEqual([]);
    expect([...result.root.querySelectorAll<HTMLTextAreaElement>('textarea')].map((area) => area.value)).toEqual(['First', 'Second', 'Third']);
  });

  for (const [lang, upAnnouncement, downAnnouncement] of [['vi', 'Đã chuyển đoạn 2 tới vị trí 1', 'Đã chuyển đoạn 1 tới vị trí 2'], ['en', 'Moved paragraph 2 to position 1', 'Moved paragraph 1 to position 2']] as const) {
    it(`moves up and down with no CDK directives in the DOM in ${lang}, then focuses and announces the visible order`, async () => {
      const { fixture, root, intents, announcer } = await setup(false, lang);
      expect(fixture.debugElement.query(By.directive(CdkDropList))).toBeNull();
      expect(fixture.debugElement.query(By.directive(CdkDrag))).toBeNull();
      expect(fixture.debugElement.query(By.directive(CdkDragHandle))).toBeNull();
      const before = [...root.querySelectorAll('nts-paragraph-card')];
      const up = before[1]!.querySelector<HTMLButtonElement>('[data-action="move-up"]')!;
      up.focus();
      up.click();
      expect(announcer.messages).toEqual([]);
      fixture.detectChanges();
      await fixture.whenStable();
      expect([...root.querySelectorAll('nts-paragraph-card')]).toEqual([before[1], before[0], before[2]]);
      expect(announcer.messages).toEqual([{ text: upAnnouncement, focusAddress: 'p:0', focusText: 'Second' }]);
      root.querySelector<HTMLButtonElement>('[data-action="move-down"]')!.click();
      fixture.detectChanges();
      await fixture.whenStable();
      expect([...root.querySelectorAll('nts-paragraph-card')]).toEqual(before);
      expect(intents).toEqual([{ kind: 'moveParagraph', from: 1, to: 0 }, { kind: 'moveParagraph', from: 0, to: 1 }]);
      expect(announcer.messages[1]).toEqual({ text: downAnnouncement, focusAddress: 'p:1', focusText: 'Second' });
    });
  }

  it('does not announce when the container refuses the move', async () => {
    const { fixture, root, intents, announcer } = await setup(false, 'vi', false);
    root.querySelector<HTMLButtonElement>('[data-action="move-down"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(intents).toEqual([{ kind: 'moveParagraph', from: 0, to: 1 }]);
    expect(announcer.messages).toEqual([]);
    expect(root.querySelector<HTMLTextAreaElement>('textarea')?.value).toBe('First');
  });

  it('ignores a drop in read-only state and removes all structure controls', async () => {
    const result = await setup();
    result.fixture.componentRef.setInput('readOnly', true);
    result.fixture.detectChanges();
    await result.fixture.whenStable();
    drop(result, 1, 0);
    expect(result.intents).toEqual([]);
    expect(result.root.querySelectorAll('.paragraph-card__actions button, .paragraph-card__handle')).toHaveLength(0);
    expect(result.announcer.messages).toEqual([]);
  });
});
