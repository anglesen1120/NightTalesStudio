import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import episodesVi from '../../../../assets/i18n/episodes/vi.json';
import qualityVi from '../../../../assets/i18n/quality/vi.json';
import errorsVi from '../../../../assets/i18n/errors/vi.json';
import episodesEn from '../../../../assets/i18n/episodes/en.json';
import qualityEn from '../../../../assets/i18n/quality/en.json';
import errorsEn from '../../../../assets/i18n/errors/en.json';
import { onEmit } from '../testing/on-emit';
import { InlineAnnotationComponent } from './inline-annotation.component';
import type { InlineAnnotationViewModel } from './inline-annotation.model';

const translations = TranslocoTestingModule.forRoot({
  langs: { vi: episodesVi, 'episodes/vi': episodesVi, 'quality/vi': qualityVi, 'errors/vi': errorsVi, en: episodesEn, 'episodes/en': episodesEn, 'quality/en': qualityEn, 'errors/en': errorsEn },
  translocoConfig: { availableLangs: ['vi', 'en'], defaultLang: 'vi', fallbackLang: 'vi', reRenderOnLangChange: true },
  preloadLangs: true,
});
const note: InlineAnnotationViewModel = {
  id: 'ann-p-0-Q-LEN-01', address: 'p:0', ruleId: 'Q-LEN-01', severity: 'blocking', kind: 'objective',
  messageKey: 'episodes.annotations.unknown', evidence: '<img src=x onerror=alert(1)>', repairDirection: 'Shorten this passage.',
  addressLabelKey: 'episodes.editor.segment.paragraph', addressLabelIndex: 1, canGoTo: false,
};
async function setup(annotation = note) {
  await TestBed.configureTestingModule({ imports: [InlineAnnotationComponent, translations], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(InlineAnnotationComponent);
  fixture.componentRef.setInput('annotation', annotation);
  fixture.detectChanges(); await fixture.whenStable();
  return { fixture, root: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}
afterEach(() => TestBed.resetTestingModule());

describe('InlineAnnotationComponent', () => {
  it('shows an icon and visible severity text with safely interpolated evidence and repair direction', async () => {
    const { root } = await setup();
    expect(root.querySelector('[role="note"]')?.id).toBe(note.id);
    expect(root.querySelector('.inline-annotation__severity svg')).not.toBeNull();
    expect(root.querySelector('.inline-annotation__severity')?.textContent).toContain(episodesVi.annotations.blocking);
    expect(root.textContent).toContain(note.evidence);
    expect(root.textContent).toContain(note.repairDirection);
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('[aria-live]')).toBeNull();
  });

  it('does not offer go-to under its own textarea', async () => {
    const { root } = await setup();
    expect(root.querySelector('[role="note"]')).not.toBeNull();
    expect(root.querySelector('button')).toBeNull();
  });

  it('emits a panel focus intent without stealing focus', async () => {
    const { root, component } = await setup({ ...note, canGoTo: true });
    const emitted: InlineAnnotationViewModel[] = [];
    onEmit(component.focusRequested, (value) => emitted.push(value));
    const button = root.querySelector<HTMLButtonElement>('button')!;
    expect(button.getAttribute('aria-describedby')).toBe(note.id);
    button.click();
    expect(emitted).toEqual([{ ...note, canGoTo: true }]);
  });

  it('keeps distinct address/rule IDs and gives a panel copy its own DOM ID', async () => {
    const { fixture, root } = await setup({ ...note, canGoTo: true });
    const other = TestBed.createComponent(InlineAnnotationComponent);
    other.componentRef.setInput('annotation', { ...note, id: 'ann-hook-Q-LEN-01', address: 'hook' });
    other.detectChanges(); await other.whenStable();
    expect((other.nativeElement as HTMLElement).querySelector('[role="note"]')?.id).not.toBe(root.querySelector('[role="note"]')?.id);
    fixture.componentRef.setInput('noteId', note.id + '__panel');
    fixture.detectChanges();
    expect(root.querySelector('[role="note"]')?.id).toBe(note.id + '__panel');
    expect(root.querySelector('button')?.getAttribute('aria-describedby')).toBe(note.id + '__panel');
  });

  it('shows advisory text, compact address labels and no go-to for unresolved notes', async () => {
    const { fixture, root } = await setup({ ...note, severity: 'advisory', address: null, addressLabelKey: 'episodes.annotations.unresolved', addressLabelIndex: null, canGoTo: false });
    fixture.componentRef.setInput('compact', true);
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.textContent).toContain(episodesVi.annotations.advisory);
    expect(root.textContent).toContain(episodesVi.annotations.unresolved);
    expect(root.querySelector('button')).toBeNull();
  });

  it('renders severity and action labels in English after a language change', async () => {
    const { fixture, root } = await setup({ ...note, canGoTo: true });
    TestBed.inject(TranslocoService).setActiveLang('en');
    fixture.detectChanges(); await fixture.whenStable();
    expect(root.querySelector('.inline-annotation__severity')?.textContent).toContain(episodesEn.annotations.blocking);
    expect(root.querySelector('button')?.textContent).toContain(episodesEn.annotations.goTo);
    expect(root.querySelector('.inline-annotation__message')?.textContent).toContain(episodesEn.annotations.unknown);
  });

});
