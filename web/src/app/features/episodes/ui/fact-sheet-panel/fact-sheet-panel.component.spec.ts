import { DeferBlockBehavior, DeferBlockState, TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { onEmit } from '@shared/ui/testing/on-emit';
import { ZONELESS_TEST_PROVIDERS } from '../../../../testing/providers';
import { researchCopy, researchTranslations, source, fact, sheet } from '../../testing/research-ui.testing';
import { FactSheetPanelComponent } from './fact-sheet-panel.component';
afterEach(() => TestBed.resetTestingModule());
async function setup() {
  await TestBed.configureTestingModule({ imports: [FactSheetPanelComponent, researchTranslations()], providers: [...ZONELESS_TEST_PROVIDERS], deferBlockBehavior: DeferBlockBehavior.Manual }).compileComponents();
  const fixture = TestBed.createComponent(FactSheetPanelComponent); await fixture.whenStable();
  return {fixture, component:fixture.componentInstance, root:fixture.nativeElement as HTMLElement};
}
const button = (root:HTMLElement, label:string) => [...root.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.trim() === label)!;
describe('FactSheetPanelComponent', () => {
  it('renders null as empty, permits generate only with fetched sources and disables save/approve', async () => {
    const {fixture,root} = await setup(); expect(root.textContent).toContain(researchCopy.factSheet.empty); expect(root.querySelector('nts-error-state')).toBeNull();
    expect(button(root,researchCopy.factSheet.save).disabled).toBe(true); expect(button(root,researchCopy.factSheet.approve).disabled).toBe(true);
    fixture.componentRef.setInput('canGenerate', true); await fixture.whenStable(); expect(button(root,researchCopy.factSheet.generate).disabled).toBe(false);
  });
  it('renders its deferred ledger and approved read-only state with Edit only', async () => {
    const {fixture,root} = await setup(); fixture.componentRef.setInput('sheet', sheet({status:'approved'})); fixture.componentRef.setInput('facts',[fact()]); fixture.componentRef.setInput('sources',[source()]); await fixture.whenStable();
    const blocks = await fixture.getDeferBlocks(); expect(blocks).toHaveLength(1); await blocks[0]!.render(DeferBlockState.Complete); await fixture.whenStable();
    expect(root.querySelector('nts-fact-row')).not.toBeNull(); expect(root.querySelector('textarea, mat-select')).toBeNull(); expect(button(root,researchCopy.factSheet.edit)).toBeDefined(); expect(root.textContent).not.toContain(researchCopy.factSheet.approve);
  });
  it('requires a source to add facts and low confidence does not block approval', async () => {
    const {fixture,component,root} = await setup(); let added:readonly string[]=[]; onEmit(component.addFact, value => {added=value;}); fixture.componentRef.setInput('sheet',sheet()); fixture.componentRef.setInput('facts',[fact({confidence:'low'})]); fixture.componentRef.setInput('canApprove',true); await fixture.whenStable();
    expect(button(root,researchCopy.research.facts.add).disabled).toBe(true); expect(button(root,researchCopy.factSheet.approve).disabled).toBe(false);
    fixture.componentRef.setInput('sources',[source()]); await fixture.whenStable(); button(root,researchCopy.research.facts.add).click(); expect(added).toEqual(['source-a']);
  });
  it('blocks network actions during Retry-After without freezing local edits', async () => {
    const {fixture,root} = await setup();
    fixture.componentRef.setInput('sheet',sheet()); fixture.componentRef.setInput('facts',[fact()]); fixture.componentRef.setInput('sources',[source()]);
    fixture.componentRef.setInput('canSave',true); fixture.componentRef.setInput('canApprove',true); fixture.componentRef.setInput('canGenerate',true); fixture.componentRef.setInput('retryBlocked',true);
    await fixture.whenStable();
    expect(button(root,researchCopy.factSheet.save).disabled).toBe(true); expect(button(root,researchCopy.factSheet.approve).disabled).toBe(true); expect(button(root,researchCopy.factSheet.generate).disabled).toBe(true);
    expect(button(root,researchCopy.research.facts.add).disabled).toBe(false); expect(root.textContent).toContain(researchCopy.factSheet.waitForRetry);
  });
  it('contains no credit, price or balance UI', async () => {
    const {root} = await setup(); expect(root.textContent).not.toMatch(/\b(credits?|cost|price|balance)\b|tín dụng|chi phí|số dư/i);
  });
  it('uses the first fetched source rather than a pending first row', async () => {
    const {fixture,component,root}=await setup();let cited:readonly string[]=[];onEmit(component.addFact,value=>{cited=value;});
    fixture.componentRef.setInput('sheet',sheet());fixture.componentRef.setInput('sources',[source({status:'pending'})]);await fixture.whenStable();
    expect(button(root,researchCopy.research.facts.add).disabled).toBe(true);expect(root.textContent).toContain(researchCopy.research.facts.requiresSource);
    fixture.componentRef.setInput('sources',[source({status:'pending'}),source({id:'fetched',status:'fetched'})]);await fixture.whenStable();
    button(root,researchCopy.research.facts.add).click();expect(cited).toEqual(['fetched']);
  });
  it('shows the reopen hint only for a draft sheet during an active run', async () => {
    const {fixture,root}=await setup();fixture.componentRef.setInput('sheet',sheet());await fixture.whenStable();
    expect(root.textContent).not.toContain(researchCopy.factSheet.reopenStoryHint);
    fixture.componentRef.setInput('running',true);await fixture.whenStable();expect(root.textContent).toContain(researchCopy.factSheet.reopenStoryHint);
    fixture.componentRef.setInput('sheet',sheet({status:'approved'}));await fixture.whenStable();expect(root.textContent).not.toContain(researchCopy.factSheet.reopenStoryHint);
  });
});
