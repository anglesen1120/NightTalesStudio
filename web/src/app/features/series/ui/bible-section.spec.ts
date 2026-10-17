import { TestBed } from '@angular/core/testing';
import { FormArray, FormControl, FormGroup } from '@angular/forms';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';
import coreEn from '../../../../assets/i18n/core/en.json';
import seriesEn from '../../../../assets/i18n/series/en.json';
import { ZONELESS_TEST_PROVIDERS } from '../../../testing/providers';
import { onEmit } from '@shared/ui/testing/on-emit';
import { BibleSection, type BibleEntryControls, type BibleEntryForm } from './bible-section';

afterEach(() => TestBed.resetTestingModule());
function character(name = 'Lan'): BibleEntryForm {
  return new FormGroup<BibleEntryControls>({ id: new FormControl(`ch-${name}`, { nonNullable: true }), name: new FormControl(name, { nonNullable: true }), description: new FormControl('', { nonNullable: true }) });
}
async function setup(rows = new FormArray<BibleEntryForm>([character()])) {
  await TestBed.configureTestingModule({ imports: [BibleSection, TranslocoTestingModule.forRoot({ langs: { en: { ...coreEn, series: seriesEn } }, translocoConfig: { availableLangs: ['en'], defaultLang: 'en' }, preloadLangs: true })], providers: ZONELESS_TEST_PROVIDERS }).compileComponents();
  const fixture = TestBed.createComponent(BibleSection);
  fixture.componentRef.setInput('kind', 'characters');
  fixture.componentRef.setInput('rows', rows);
  await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement, rows };
}
describe('BibleSection', () => {
  it('groups fields with a legend and names the remove action after its entry', async () => {
    const { root } = await setup();
    expect(root.querySelector('fieldset > legend')).not.toBeNull();
    expect(root.querySelector('button[aria-label]')?.getAttribute('aria-label')).toContain('Lan');
    expect(root.querySelectorAll('nts-char-counter')).toHaveLength(2);
  });
  it('emits add and removal intents without owning the form array', async () => {
    const { component, root, rows } = await setup();
    let adds = 0; const removals: number[] = [];
    onEmit(component.add, () => adds++); onEmit(component.remove, index => removals.push(index));
    const buttons = root.querySelectorAll<HTMLButtonElement>('button');
    buttons[0]!.click(); buttons[1]!.click();
    expect(removals).toEqual([0]); expect(adds).toBe(1); expect(rows.length).toBe(1);
  });
  it('disables add at thirty entries and never offers summary creation', async () => {
    const { fixture, root } = await setup(new FormArray<BibleEntryForm>(Array.from({ length: 30 }, (_, index) => character(String(index)))));
    expect(root.querySelector<HTMLButtonElement>('fieldset > button')?.disabled).toBe(true);
    fixture.componentRef.setInput('rows', new FormArray<BibleEntryForm>([]));
    fixture.componentRef.setInput('kind', 'summaries');
    await fixture.whenStable();
    expect(root.querySelector('button')).toBeNull();
  });
});
