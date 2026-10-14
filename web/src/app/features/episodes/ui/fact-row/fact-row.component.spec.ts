import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { researchTranslations, source, fact } from '../../testing/research-ui.testing';
import { FactRowComponent } from './fact-row.component';
afterEach(() => TestBed.resetTestingModule());
async function setup() {
  await TestBed.configureTestingModule({ imports: [FactRowComponent, researchTranslations()], providers: [...ZONELESS_TEST_PROVIDERS] }).compileComponents();
  const fixture = TestBed.createComponent(FactRowComponent); fixture.componentRef.setInput('fact', fact()); fixture.componentRef.setInput('index', 3); fixture.componentRef.setInput('sources', [source()]); await fixture.whenStable();
  return { fixture, component: fixture.componentInstance, root: fixture.nativeElement as HTMLElement };
}
describe('FactRowComponent', () => {
  it('shows source aliases, safe links, note labels and indexed delete names', async () => {
    const { fixture, root } = await setup(); expect(root.querySelector('.source-chip')?.textContent).toContain('S1'); expect(root.querySelector('a')?.getAttribute('rel')).toBe('noopener noreferrer');
    expect(root.querySelector('button.delete')?.getAttribute('aria-label')).toBe('Delete fact 3');
    fixture.componentRef.setInput('fact', fact({ sources: [{sourceId:'source-a',alias:'N1',title:'Secret note',url:null,isUserNote:true}] })); await fixture.whenStable();
    expect(root.querySelector('.source-chip')?.textContent).toContain('your note'); expect(root.querySelector('.source-chip')?.textContent).not.toContain('Secret note');
  });
  it('keeps low confidence visible and every non-null overlap advisory visible', async () => {
    const { fixture, root } = await setup(); fixture.componentRef.setInput('fact', fact({confidence:'low', sourceOverlap:0})); await fixture.whenStable();
    expect(root.textContent).toContain('Needs checking'); expect(root.textContent).toContain('nearly verbatim');
  });
  it('renders approved statements as text and blocks all edit outputs', async () => {
    const { fixture, root, component } = await setup(); let edits = 0; onEmit(component.patch, () => edits++);
    fixture.componentRef.setInput('readonly', true); fixture.componentRef.setInput('fact', fact({statement:'<img onerror=alert(1)>', sources:[{sourceId:'source-a',alias:'S1',title:'unsafe',url:'javascript:alert(1)',isUserNote:false}]})); await fixture.whenStable();
    expect(root.querySelector('textarea, mat-select, button.delete, img, a')).toBeNull(); expect(root.textContent).toContain('<img onerror=alert(1)>'); component.changeConfidence('low'); expect(edits).toBe(0);
  });
  it('announces per-fact errors and offers to drop a missing source', async () => {
    const { fixture, root, component } = await setup(); let dropped = ''; onEmit(component.dropSource, value => { dropped = value; }); fixture.componentRef.setInput('sources', []); fixture.componentRef.setInput('invalid', true); await fixture.whenStable();
    expect(root.querySelector('.error')?.textContent?.trim()).not.toBe(''); root.querySelector<HTMLButtonElement>('.source-chip button')!.click(); expect(dropped).toBe('source-a');
  });
  it('binds indexed server fields on typed controls while preserving local validators', async () => {
    const { fixture, component } = await setup();
    fixture.componentRef.setInput('fact', fact({ statement: 'x'.repeat(601) }));
    fixture.componentRef.setInput('fieldError', { code: 'fact_sheet_invalid_content', status: 400, kind: 'validation', fieldErrors: { 'content.facts.2.statement': ['invalid'] } });
    await fixture.whenStable();
    expect(component.form.controls.statement.hasError('maxlength')).toBe(true);
    expect(Object.keys(component.form.controls.statement.errors ?? {})).toHaveLength(2);
  });
});
