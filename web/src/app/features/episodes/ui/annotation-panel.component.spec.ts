import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import type { InlineAnnotationViewModel } from '@shared/ui';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import qualityVi from '../../../../assets/i18n/quality/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import { AnnotationPanelComponent } from './annotation-panel.component';
import type { AnnotationPanelViewModel } from './annotation.view-model';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: episodesVi, 'episodes/vi': episodesVi, 'quality/vi': qualityVi, 'errors/vi': errorsVi },
  translocoConfig: { availableLangs: ['vi'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});
const note: InlineAnnotationViewModel = {
  id: 'ann-hook-first', address: 'hook', ruleId: 'first', severity: 'blocking', kind: 'objective',
  messageKey: 'episodes.annotations.unknown', evidence: '', repairDirection: null,
  addressLabelKey: 'episodes.editor.segment.hook', addressLabelIndex: null, canGoTo: true,
};
const viewModel: AnnotationPanelViewModel = { count: 3, groups: [
  { kind: 'objective', items: [note, { ...note, id: 'ann-meta-second', address: 'meta', severity: 'advisory', addressLabelKey: 'episodes.annotations.meta', canGoTo: false }] },
  { kind: 'taste', items: [{ ...note, id: 'ann-unresolved-third', kind: 'taste', address: null, addressLabelKey: 'episodes.annotations.unresolved', canGoTo: false }] },
] };
async function setup(model = viewModel) {
  await TestBed.configureTestingModule({ imports: [AnnotationPanelComponent, translations], providers: ZONELESS_TEST_PROVIDERS, deferBlockBehavior: DeferBlockBehavior.Manual }).compileComponents();
  const fixture = TestBed.createComponent(AnnotationPanelComponent);
  fixture.componentRef.setInput('viewModel', model);
  fixture.detectChanges(); await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}
afterEach(() => TestBed.resetTestingModule());

describe('AnnotationPanelComponent', () => {
  it('reserves its body before deferring, then keeps grouped order and unique panel IDs', async () => {
    const { fixture, root } = await setup();
    expect(root.querySelector('.annotation-panel__count')?.textContent).toContain('3');
    expect(root.querySelector('.annotation-panel__placeholder.annotation-panel__body')).not.toBeNull();
    expect(root.querySelector('[role="note"]')).toBeNull();
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    fixture.detectChanges(); await fixture.whenStable();
    expect([...root.querySelectorAll('h3')].map((heading) => heading.textContent?.trim())).toEqual([episodesVi.annotations.objective, episodesVi.annotations.taste]);
    expect([...root.querySelectorAll('[role="note"]')].map((element) => element.id)).toEqual(['ann-hook-first-panel', 'ann-meta-second-panel', 'ann-unresolved-third-panel']);
    expect(root.textContent).toContain(episodesVi.annotations.meta);
    expect(root.textContent).toContain(episodesVi.annotations.unresolved);
    expect(root.querySelector('.annotation-panel__body')).not.toBeNull();
    expect(root.querySelector('[aria-live]')).toBeNull();
  });

  it('renders the empty translation after the deferred body loads', async () => {
    const { fixture, root } = await setup({ count: 0, groups: [] });
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.annotation-panel__empty')?.textContent).toContain(episodesVi.annotations.none);
    expect(root.querySelectorAll('[role="note"]')).toHaveLength(0);
  });

  it('forwards go-to only for an address with a segment control', async () => {
    const { fixture, root, component } = await setup();
    const emitted: InlineAnnotationViewModel[] = [];
    onEmit(component.focusRequested, (item) => emitted.push(item));
    const blocks = await fixture.getDeferBlocks();
    await blocks[0]!.render(DeferBlockState.Complete);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelectorAll('button')).toHaveLength(1);
    root.querySelector<HTMLButtonElement>('button')!.click();
    expect(emitted).toEqual([note]);
  });
});
